using Detection;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace Swarm
{
    public struct SwarmRenderMeshIndirectStats
    {
        public SwarmRenderPipelineCase RenderCase;
        public SwarmSubmissionMode SubmissionMode;

        public int CaseIndex;
        public int InstanceCount;
        public int InstanceCapacity;

        public int MeshCount;
        public int MaterialCount;
        public int ApiCallCount;
        public int DrawCommandCount;
        public int CommandsPerSubMesh;

        public int ParticleUploadBytesPerFrame;
        public int AnimationUploadBytesPerFrame;
        public int IndirectArgsUploadBytesPerFrame;
        public int TotalUploadBytesPerFrame;

        public int MatrixBufferSizeBytes;
        public int FrameIndex;
        public bool IsValid;

        public static SwarmRenderMeshIndirectStats CreateDefault()
        {
            var value = new SwarmRenderMeshIndirectStats();

            value.ApplySubmissionMode(
                SwarmSubmissionMode.Integrated);

            return value;
        }

        public void ApplySubmissionMode(
            SwarmSubmissionMode mode)
        {
            SubmissionMode = mode;

            RenderCase =
                SwarmRenderPipelineCaseUtility.GetCase(
                    SwarmRenderBackend.RenderMeshIndirect,
                    mode);

            CaseIndex =
                SwarmRenderPipelineCaseUtility.GetCaseIndex(
                    RenderCase);
        }

        public void MarkInvalid(
            int frameIndex)
        {
            FrameIndex = frameIndex;
            IsValid = false;

            InstanceCount = 0;
            MaterialCount = 0;
            ApiCallCount = 0;
            DrawCommandCount = 0;
            CommandsPerSubMesh = 0;

            ParticleUploadBytesPerFrame = 0;
            AnimationUploadBytesPerFrame = 0;
            IndirectArgsUploadBytesPerFrame = 0;
            TotalUploadBytesPerFrame = 0;
        }
    }

    /// <summary>
    /// 동일한 ECS 시뮬레이션 결과를
    /// Graphics.RenderMeshIndirect로 제출하는 실험 전용 렌더러.
    ///
    /// Integrated:
    /// - 유효 서브메시당 간접 명령 1개
    /// - 유효 서브메시당 API 호출 1회
    ///
    /// Split:
    /// - 유효 서브메시당 인스턴스 범위를 K개로 분할
    /// - D3D11/D3D12 호환성을 위해 명령별 RenderMeshIndirect 호출
    /// - 셰이더에는 _InstanceOffset으로 전역 인스턴스 시작 위치 전달
    ///
    /// Materials 배열만 사용하며
    /// 단일 Material fallback은 사용하지 않는다.
    /// </summary>
    public sealed class SwarmRenderMeshIndirectRenderer :
        MonoBehaviour,
        ISwarmRendererInstrumentationSource
    {
        private const int ParticleStrideBytes = 44;
        private const int AnimationStrideBytes = 16;
        private const int MatrixStrideBytes = 64;
        private const int ThreadGroupSize = 64;

        // 2x2 실험에서 두 백엔드에 공통 적용할 고정 렌더 상태.
        private const int ForcedMeshLod = 0;
        private const uint AllRenderingLayers = uint.MaxValue;

        private struct RmiParticleData
        {
            public float3 UnitPos;
            public float3 LocalOffset;

            public float FacingYaw;
            public float Phase;
            public float PhaseSpeed;
            public float MotionScale;
            public float RenderScale;
        }

        private struct RmiAnimationPayload
        {
            public int ClipIndex;
            public float NormalizedTime;
            public float PlaybackSpeed;
            public float Padding;
        }

        private struct RmiSkinningClipInfo
        {
            public int StartFrame;
            public int FrameCount;
            public float Length;
            public float Padding;
        }

        [Header("Assets")]
        [Tooltip("기존 프로토타입에서 사용하던 인스턴스 행렬 생성 Compute Shader")]
        [SerializeField]
        private ComputeShader swarmCompute;

        [Tooltip("서브메시 순서와 동일하게 배치할 실제 렌더 머터리얼 배열")]
        [SerializeField]
        private Material[] materials;

        [Tooltip("GPU 스키닝을 사용하지 않을 때 렌더링할 기본 메시")]
        [SerializeField]
        private Mesh mesh;

        [Tooltip("GPU 스키닝용 베이크 클립 세트")]
        [SerializeField]
        private GpuSkinningClipSet gpuSkinningClipSet;

        [Header("Submission")]
        [SerializeField]
        private SwarmSubmissionMode submissionMode =
            SwarmSubmissionMode.Integrated;

        [Tooltip("Split 조건에서 서브메시당 생성할 간접 명령 수")]
        [Min(1)]
        [SerializeField]
        private int splitDrawCommandCount = 4;

        [Header("Capacity")]
        [Tooltip("초기 인스턴스 버퍼 용량")]
        [Min(1)]
        [SerializeField]
        private int initialInstanceCapacity = 4096;

        [Header("Visual")]
        [Min(0.001f)]
        [SerializeField]
        private float particleScale = 1f;

        [SerializeField]
        private float yOffset;

        [Header("GPU Skinning")]
        [SerializeField]
        private bool enableGpuSkinning = true;

        [Header("Bounds")]
        [SerializeField]
        private Vector3 boundsCenter =
            Vector3.zero;

        [SerializeField]
        private Vector3 boundsSize =
            Vector3.one * 10000f;

        [Header("Instrumentation")]
        [Tooltip("BenchmarkRunner가 CurrentStats를 읽을 수 있도록 구조 계측을 활성화합니다.")]
        [SerializeField]
        private bool enableInstrumentation = true;

        [Header("Debug")]
        [SerializeField]
        private bool logConfigurationWarnings = true;

        private GraphicsBuffer _matrixBuffer;
        private GraphicsBuffer _particleBuffer;
        private GraphicsBuffer _animationBuffer;

        private GraphicsBuffer _skinningMatrixBuffer;
        private GraphicsBuffer _skinningClipBuffer;

        private GraphicsBuffer[] _commandBuffers;

        private GraphicsBuffer.IndirectDrawIndexedArgs[][]
            _commandData;

        private NativeArray<RmiParticleData> _particles;

        private NativeArray<RmiAnimationPayload>
            _animations;

        private int _instanceCapacity;

        private int _commandSubMeshCount;
        private int _commandCapacity;

        private int _matrixKernel = -1;

        private ComputeShader _cachedCompute;

        private GpuSkinningClipSet _uploadedClipSet;

        private int _uploadedMatrixCount;
        private int _uploadedClipCount;
        private int _uploadedBoneCount;

        private World _world;
        private EntityManager _entityManager;
        private EntityQuery _query;

        private bool _queryReady;

        private MaterialPropertyBlock _propertyBlock;

        private SwarmRenderMeshIndirectStats _stats;

        private static readonly int ParticlesId =
            Shader.PropertyToID(
                "_Particles");

        private static readonly int VisibleMatricesId =
            Shader.PropertyToID(
                "_VisibleMatrices");

        private static readonly int TotalParticleCountId =
            Shader.PropertyToID(
                "_TotalParticleCount");

        private static readonly int ParticleScaleId =
            Shader.PropertyToID(
                "_ParticleScale");

        private static readonly int YOffsetId =
            Shader.PropertyToID(
                "_YOffset");

        private static readonly int AnimationPayloadsId =
            Shader.PropertyToID(
                "_SoldierAnimationPayloads");

        private static readonly int SkinningMatricesId =
            Shader.PropertyToID(
                "_SkinningMatrices");

        private static readonly int SkinningClipInfosId =
            Shader.PropertyToID(
                "_SkinningClipInfos");

        private static readonly int SkinningEnabledId =
            Shader.PropertyToID(
                "_SkinningEnabled");

        private static readonly int SkinningBoneCountId =
            Shader.PropertyToID(
                "_SkinningBoneCount");

        private static readonly int SkinningClipCountId =
            Shader.PropertyToID(
                "_SkinningClipCount");

        private static readonly int InstanceOffsetId =
            Shader.PropertyToID(
                "_InstanceOffset");

        public SwarmRenderMeshIndirectStats CurrentStats =>
            _stats;

        public SwarmSubmissionMode SubmissionMode =>
            submissionMode;

        public int ActiveInstanceCount =>
            _stats.InstanceCount;

        public bool InstrumentationEnabled =>
            enableInstrumentation;

        public SwarmRenderPipelineCase InstrumentationCase =>
            SwarmRenderPipelineCaseUtility.GetCase(
                SwarmRenderBackend.RenderMeshIndirect,
                submissionMode);

        private void OnEnable()
        {
            _propertyBlock ??=
                new MaterialPropertyBlock();

            _stats =
                SwarmRenderMeshIndirectStats.CreateDefault();

            EnsureQuery();

            EnsureInstanceBuffers(
                initialInstanceCapacity);
        }

        private void OnDisable()
        {
            ReleaseResources();
        }

        private void OnDestroy()
        {
            ReleaseResources();
        }

        private void OnValidate()
        {
            initialInstanceCapacity =
                math.max(
                    1,
                    initialInstanceCapacity);

            splitDrawCommandCount =
                math.max(
                    1,
                    splitDrawCommandCount);

            particleScale =
                math.max(
                    0.001f,
                    particleScale);

            boundsSize.x =
                math.max(
                    0.001f,
                    boundsSize.x);

            boundsSize.y =
                math.max(
                    0.001f,
                    boundsSize.y);

            boundsSize.z =
                math.max(
                    0.001f,
                    boundsSize.z);

            _matrixKernel = -1;
            _cachedCompute = null;
        }

        public void SetSubmissionMode(
            SwarmSubmissionMode mode)
        {
            submissionMode = mode;
        }

        public void SetRenderPipelineCase(
            SwarmRenderPipelineCase renderCase)
        {
            if (!SwarmRenderPipelineCaseUtility
                    .IsRenderMeshIndirect(renderCase))
            {
                if (logConfigurationWarnings)
                {
                    Debug.LogWarning(
                        $"{nameof(SwarmRenderMeshIndirectRenderer)}: " +
                        $"{renderCase}는 RenderMeshIndirect 조건이 아닙니다.",
                        this);
                }

                return;
            }

            submissionMode =
                SwarmRenderPipelineCaseUtility
                    .GetSubmissionMode(renderCase);
        }

        public void SetInstrumentationEnabled(
            bool enabled)
        {
            enableInstrumentation = enabled;

            if (!enabled)
            {
                _stats = default;
            }
        }

        public bool TryGetInstrumentationSample(
            out SwarmRendererInstrumentationSample sample)
        {
            sample = default;

            if (!enableInstrumentation ||
                !_stats.IsValid)
            {
                return false;
            }

            sample = new SwarmRendererInstrumentationSample
            {
                IsValid = true,
                FrameIndex = _stats.FrameIndex,
                RenderCase = _stats.RenderCase,
                SubmissionMode = _stats.SubmissionMode,
                InstanceCount = _stats.InstanceCount,
                ActiveSubMeshCount = _stats.MaterialCount,
                ActiveMaterialCount = _stats.MaterialCount,
                CommandsPerSubMesh = _stats.CommandsPerSubMesh,
                SubmittedCommandCount = _stats.DrawCommandCount,
                ApiCallCount = _stats.ApiCallCount,
                UploadedBytesPerFrame =
                    _stats.TotalUploadBytesPerFrame,
                ComputeDispatchCount = 1,
                BatchCount = 0,
                DrawRangeCount = 0,
                BatchDrawCommandCount = 0
            };

            return true;
        }

        /// <summary>
        /// BenchmarkRunner가 측정 시작 전에 두 렌더러의 실험 통제 조건을
        /// 비교할 수 있도록 현재 구성값을 반환한다.
        /// 이 메서드는 GPU 작업이나 ECS 순회를 수행하지 않는다.
        /// </summary>
        public SwarmRendererControlState GetControlState()
        {
            bool gpuSkinningActive =
                IsSkinningValid();

            Mesh renderMesh =
                gpuSkinningActive
                    ? gpuSkinningClipSet.SourceMesh
                    : mesh;

            return new SwarmRendererControlState
            {
                CullingMode =
                    SwarmCullingMode.AllVisible,

                BoundsCenter =
                    boundsCenter,

                BoundsSize =
                    boundsSize,

                ParticleScale =
                    particleScale,

                YOffset =
                    yOffset,

                GpuSkinningRequested =
                    enableGpuSkinning,

                GpuSkinningActive =
                    gpuSkinningActive,

                GpuSkinningClipSet =
                    gpuSkinningClipSet,

                RenderMesh =
                    renderMesh,

                SplitCommandCount =
                    splitDrawCommandCount,

                ActiveMaterialCount =
                    CountConfiguredMaterials(
                        renderMesh),

                Layer =
                    gameObject.layer,

                RenderingLayerMask =
                    AllRenderingLayers,

                ShadowCastingMode =
                    ShadowCastingMode.Off,

                ReceiveShadows =
                    false,

                MotionVectorMode =
                    MotionVectorGenerationMode
                        .ForceNoMotion,

                ForcedMeshLod =
                    ForcedMeshLod
            };
        }

        private void MarkInstrumentationInvalid()
        {
            if (!enableInstrumentation)
            {
                return;
            }

            _stats.ApplySubmissionMode(
                submissionMode);

            _stats.MarkInvalid(
                Time.frameCount);
        }

        private void Update()
        {
            Mesh renderMesh =
                GetRenderMesh();

            if (renderMesh == null ||
                swarmCompute == null ||
                !EnsureComputeKernel() ||
                !HasUsableMaterials(renderMesh) ||
                !EnsureQuery() ||
                _query.IsEmpty)
            {
                MarkInstrumentationInvalid();

                return;
            }

            int instanceCount =
                CollectEcsData();

            if (instanceCount <= 0)
            {
                MarkInstrumentationInvalid();

                return;
            }

            bool skinningActive =
                UploadSkinningDataIfNeeded();

            UploadSourceData(
                instanceCount);

            DispatchMatrixBuild(
                instanceCount);

            BindCommonProperties(
                skinningActive);

            int subMeshCount =
                math.min(
                    renderMesh.subMeshCount,
                    materials.Length);

            int commandsPerSubMesh =
                submissionMode ==
                SwarmSubmissionMode.Split
                    ? math.min(
                        math.max(
                            1,
                            splitDrawCommandCount),
                        instanceCount)
                    : 1;

            EnsureCommandBuffers(
                subMeshCount,
                commandsPerSubMesh);

            int validMaterialCount = 0;
            int apiCallCount = 0;
            int drawCommandCount = 0;

            var worldBounds =
                new Bounds(
                    boundsCenter,
                    boundsSize);

            for (int subMesh = 0;
                 subMesh < subMeshCount;
                 subMesh++)
            {
                Material drawMaterial =
                    materials[subMesh];

                if (drawMaterial == null)
                {
                    continue;
                }

                FillIndirectCommands(
                    renderMesh,
                    subMesh,
                    instanceCount,
                    commandsPerSubMesh);

                _commandBuffers[subMesh].SetData(
                    _commandData[subMesh],
                    0,
                    0,
                    commandsPerSubMesh);

                var renderParams =
                    new RenderParams(
                        drawMaterial)
                    {
                        worldBounds =
                            worldBounds,

                        // 그림자, 모션 벡터, LOD를 BRG 조건과 동일하게
                        // 고정하여 제출 백엔드 외의 차이를 제거한다.
                        shadowCastingMode =
                            ShadowCastingMode.Off,

                        receiveShadows =
                            false,

                        motionVectorMode =
                            MotionVectorGenerationMode
                                .ForceNoMotion,

                        layer =
                            gameObject.layer,

                        renderingLayerMask =
                            AllRenderingLayers,

                        forceMeshLod =
                            ForcedMeshLod,

                        matProps =
                            _propertyBlock
                    };

                for (int commandIndex = 0;
                     commandIndex < commandsPerSubMesh;
                     commandIndex++)
                {
                    GetCommandRange(
                        instanceCount,
                        commandsPerSubMesh,
                        commandIndex,
                        out int startInstance,
                        out int rangeCount);

                    if (rangeCount <= 0)
                    {
                        continue;
                    }

                    _propertyBlock.SetInt(
                        InstanceOffsetId,
                        startInstance);

                    renderParams.matProps =
                        _propertyBlock;

                    Graphics.RenderMeshIndirect(
                        renderParams,
                        renderMesh,
                        _commandBuffers[subMesh],
                        1,
                        commandIndex);

                    apiCallCount++;
                    drawCommandCount++;
                }

                validMaterialCount++;
            }

            UpdateStats(
                instanceCount,
                renderMesh,
                validMaterialCount,
                apiCallCount,
                drawCommandCount,
                commandsPerSubMesh);
        }

        private bool EnsureComputeKernel()
        {
            if (swarmCompute == null)
            {
                return false;
            }

            if (_cachedCompute == swarmCompute &&
                _matrixKernel >= 0)
            {
                return true;
            }

            _cachedCompute =
                swarmCompute;

            _matrixKernel =
                swarmCompute.FindKernel(
                    "CSMain");

            return _matrixKernel >= 0;
        }

        private bool EnsureQuery()
        {
            World world =
                World.DefaultGameObjectInjectionWorld;

            if (world == null ||
                !world.IsCreated)
            {
                _queryReady = false;

                return false;
            }

            if (_queryReady &&
                _world == world)
            {
                return true;
            }

            _world = world;

            _entityManager =
                world.EntityManager;

            _query =
                _entityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<UnitTag>(),
                    ComponentType.ReadOnly<LocalTransform>(),
                    ComponentType.ReadOnly<UnitLocomotion>(),
                    ComponentType.ReadOnly<UnitAnimationState>(),
                    ComponentType.ReadOnly<UnitAnimationReference>(),
                    ComponentType.ReadOnly<
                        SwarmAnimationApproximationTuning>(),
                    ComponentType.ReadOnly<DetectionTag>(),
                    ComponentType.ReadOnly<SoldierAgent>(),
                    ComponentType.ReadOnly<SoldierVariance>(),
                    ComponentType.ReadOnly<
                        SoldierCombatState>());

            _queryReady = true;

            return true;
        }

        private int CollectEcsData()
        {
            _query.CompleteDependency();

            NativeArray<ArchetypeChunk> chunks =
                default;

            NativeArray<int> chunkOffsets =
                default;

            try
            {
                chunks =
                    _query.ToArchetypeChunkArray(
                        Allocator.TempJob);

                if (!chunks.IsCreated ||
                    chunks.Length == 0)
                {
                    return 0;
                }

                var agentHandle =
                    _entityManager
                        .GetBufferTypeHandle<
                            SoldierAgent>(true);

                var varianceHandle =
                    _entityManager
                        .GetBufferTypeHandle<
                            SoldierVariance>(true);

                var combatHandle =
                    _entityManager
                        .GetBufferTypeHandle<
                            SoldierCombatState>(true);

                var locomotionHandle =
                    _entityManager
                        .GetComponentTypeHandle<
                            UnitLocomotion>(true);

                var animationHandle =
                    _entityManager
                        .GetComponentTypeHandle<
                            UnitAnimationState>(true);

                var animationRefHandle =
                    _entityManager
                        .GetComponentTypeHandle<
                            UnitAnimationReference>(true);

                var tuningHandle =
                    _entityManager
                        .GetComponentTypeHandle<
                            SwarmAnimationApproximationTuning>(
                            true);

                var detectionHandle =
                    _entityManager
                        .GetComponentTypeHandle<
                            DetectionTag>(true);

                var transformHandle =
                    _entityManager
                        .GetComponentTypeHandle<
                            LocalTransform>(true);

                chunkOffsets =
                    new NativeArray<int>(
                        chunks.Length,
                        Allocator.TempJob,
                        NativeArrayOptions
                            .UninitializedMemory);

                int total = 0;

                for (int chunkIndex = 0;
                     chunkIndex < chunks.Length;
                     chunkIndex++)
                {
                    chunkOffsets[chunkIndex] =
                        total;

                    ArchetypeChunk chunk =
                        chunks[chunkIndex];

                    var agents =
                        chunk.GetBufferAccessor(
                            ref agentHandle);

                    var variances =
                        chunk.GetBufferAccessor(
                            ref varianceHandle);

                    for (int entityIndex = 0;
                         entityIndex < chunk.Count;
                         entityIndex++)
                    {
                        total +=
                            math.min(
                                agents[entityIndex]
                                    .Length,
                                variances[entityIndex]
                                    .Length);
                    }
                }

                if (total <= 0)
                {
                    return 0;
                }

                EnsureInstanceBuffers(
                    total);

                int clipCount =
                    IsSkinningValid()
                        ? gpuSkinningClipSet
                            .Clips.Length
                        : 0;

                var job =
                    new CollectDataJob
                    {
                        AgentHandle =
                            agentHandle,

                        VarianceHandle =
                            varianceHandle,

                        CombatHandle =
                            combatHandle,

                        LocomotionHandle =
                            locomotionHandle,

                        AnimationHandle =
                            animationHandle,

                        AnimationRefHandle =
                            animationRefHandle,

                        TuningHandle =
                            tuningHandle,

                        DetectionHandle =
                            detectionHandle,

                        TransformHandle =
                            transformHandle,

                        ChunkOffsets =
                            chunkOffsets,

                        GlobalTime =
                            Time.time,

                        SkinningClipCount =
                            clipCount,

                        OutParticles =
                            _particles,

                        OutAnimations =
                            _animations
                    };

                JobHandle handle =
                    job.ScheduleParallel(
                        _query,
                        default);

                handle.Complete();

                return total;
            }
            finally
            {
                if (chunkOffsets.IsCreated)
                {
                    chunkOffsets.Dispose();
                }

                if (chunks.IsCreated)
                {
                    chunks.Dispose();
                }
            }
        }

        private void EnsureInstanceBuffers(
            int requiredCount)
        {
            requiredCount =
                math.max(
                    1,
                    requiredCount);

            if (_instanceCapacity >= requiredCount &&
                _matrixBuffer != null &&
                _particleBuffer != null &&
                _animationBuffer != null &&
                _particles.IsCreated &&
                _animations.IsCreated)
            {
                return;
            }

            int newCapacity =
                Mathf.NextPowerOfTwo(
                    math.max(
                        requiredCount,
                        initialInstanceCapacity));

            ReleaseInstanceBuffers();

            _instanceCapacity =
                newCapacity;

            _matrixBuffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    newCapacity,
                    MatrixStrideBytes);

            _matrixBuffer.name =
                "Swarm RMI Visible Matrices";

            _particleBuffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    newCapacity,
                    ParticleStrideBytes);

            _particleBuffer.name =
                "Swarm RMI Particle Input";

            _animationBuffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    newCapacity,
                    AnimationStrideBytes);

            _animationBuffer.name =
                "Swarm RMI Animation Payload";

            _particles =
                new NativeArray<RmiParticleData>(
                    newCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions
                        .UninitializedMemory);

            _animations =
                new NativeArray<RmiAnimationPayload>(
                    newCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions
                        .UninitializedMemory);
        }

        private void UploadSourceData(
            int instanceCount)
        {
            _particleBuffer.SetData(
                _particles,
                0,
                0,
                instanceCount);

            _animationBuffer.SetData(
                _animations,
                0,
                0,
                instanceCount);
        }

        private void DispatchMatrixBuild(
            int instanceCount)
        {
            swarmCompute.SetBuffer(
                _matrixKernel,
                ParticlesId,
                _particleBuffer);

            swarmCompute.SetBuffer(
                _matrixKernel,
                VisibleMatricesId,
                _matrixBuffer);

            swarmCompute.SetInt(
                TotalParticleCountId,
                instanceCount);

            swarmCompute.SetFloat(
                ParticleScaleId,
                particleScale);

            swarmCompute.SetFloat(
                YOffsetId,
                yOffset);

            int dispatchCount =
                Mathf.CeilToInt(
                    instanceCount /
                    (float)ThreadGroupSize);

            swarmCompute.Dispatch(
                _matrixKernel,
                dispatchCount,
                1,
                1);
        }

        private void BindCommonProperties(
            bool skinningActive)
        {
            _propertyBlock.Clear();

            _propertyBlock.SetBuffer(
                VisibleMatricesId,
                _matrixBuffer);

            _propertyBlock.SetBuffer(
                AnimationPayloadsId,
                _animationBuffer);

            _propertyBlock.SetInt(
                SkinningEnabledId,
                skinningActive ? 1 : 0);

            _propertyBlock.SetInt(
                InstanceOffsetId,
                0);

            if (!skinningActive)
            {
                _propertyBlock.SetInt(
                    SkinningBoneCountId,
                    0);

                _propertyBlock.SetInt(
                    SkinningClipCountId,
                    0);

                return;
            }

            _propertyBlock.SetBuffer(
                SkinningMatricesId,
                _skinningMatrixBuffer);

            _propertyBlock.SetBuffer(
                SkinningClipInfosId,
                _skinningClipBuffer);

            _propertyBlock.SetInt(
                SkinningBoneCountId,
                gpuSkinningClipSet.BoneCount);

            _propertyBlock.SetInt(
                SkinningClipCountId,
                gpuSkinningClipSet.Clips.Length);
        }

        private void EnsureCommandBuffers(
            int subMeshCount,
            int commandCapacity)
        {
            commandCapacity =
                math.max(
                    1,
                    commandCapacity);

            bool valid =
                _commandBuffers != null &&
                _commandData != null &&
                _commandSubMeshCount ==
                subMeshCount &&
                _commandCapacity >=
                commandCapacity;

            if (valid)
            {
                return;
            }

            ReleaseCommandBuffers();

            _commandSubMeshCount =
                math.max(
                    0,
                    subMeshCount);

            _commandCapacity =
                Mathf.NextPowerOfTwo(
                    commandCapacity);

            _commandBuffers =
                new GraphicsBuffer[
                    _commandSubMeshCount];

            _commandData =
                new GraphicsBuffer
                    .IndirectDrawIndexedArgs[
                        _commandSubMeshCount][];

            for (int i = 0;
                 i < _commandSubMeshCount;
                 i++)
            {
                _commandBuffers[i] =
                    new GraphicsBuffer(
                        GraphicsBuffer.Target
                            .IndirectArguments,
                        _commandCapacity,
                        GraphicsBuffer
                            .IndirectDrawIndexedArgs
                            .size);

                _commandBuffers[i].name =
                    $"Swarm RMI Commands - SubMesh {i}";

                _commandData[i] =
                    new GraphicsBuffer
                        .IndirectDrawIndexedArgs[
                            _commandCapacity];
            }
        }

        private void FillIndirectCommands(
            Mesh renderMesh,
            int subMesh,
            int instanceCount,
            int commandCount)
        {
            GraphicsBuffer
                .IndirectDrawIndexedArgs[]
                commands =
                    _commandData[subMesh];

            uint indexCount =
                renderMesh.GetIndexCount(
                    subMesh);

            uint startIndex =
                renderMesh.GetIndexStart(
                    subMesh);

            uint baseVertex =
                unchecked(
                    (uint)renderMesh
                        .GetBaseVertex(
                            subMesh));

            for (int commandIndex = 0;
                 commandIndex < commandCount;
                 commandIndex++)
            {
                GetCommandRange(
                    instanceCount,
                    commandCount,
                    commandIndex,
                    out _,
                    out int rangeCount);

                commands[commandIndex] =
                    new GraphicsBuffer
                        .IndirectDrawIndexedArgs
                    {
                        indexCountPerInstance =
                            indexCount,

                        instanceCount =
                            (uint)math.max(
                                0,
                                rangeCount),

                        startIndex =
                            startIndex,

                        baseVertexIndex =
                            baseVertex,

                        /*
                         * 전역 인스턴스 시작 위치는
                         * 셰이더의 _InstanceOffset으로
                         * 별도 전달한다.
                         */
                        startInstance =
                            0
                    };
            }
        }

        private static void GetCommandRange(
            int instanceCount,
            int commandCount,
            int commandIndex,
            out int startInstance,
            out int rangeCount)
        {
            instanceCount =
                math.max(
                    0,
                    instanceCount);

            commandCount =
                math.max(
                    1,
                    commandCount);

            commandIndex =
                math.clamp(
                    commandIndex,
                    0,
                    commandCount - 1);

            int baseCount =
                instanceCount /
                commandCount;

            int remainder =
                instanceCount %
                commandCount;

            rangeCount =
                baseCount +
                (
                    commandIndex <
                    remainder
                        ? 1
                        : 0
                );

            startInstance =
                commandIndex *
                baseCount +
                math.min(
                    commandIndex,
                    remainder);
        }

        private bool UploadSkinningDataIfNeeded()
        {
            if (!IsSkinningValid())
            {
                ReleaseSkinningBuffers();

                return false;
            }

            Matrix4x4[] matrices =
                gpuSkinningClipSet
                    .BoneMatrices;

            GpuSkinningClipSet.Clip[] clips =
                gpuSkinningClipSet
                    .Clips;

            bool alreadyUploaded =
                _uploadedClipSet ==
                gpuSkinningClipSet &&

                _uploadedMatrixCount ==
                matrices.Length &&

                _uploadedClipCount ==
                clips.Length &&

                _uploadedBoneCount ==
                gpuSkinningClipSet
                    .BoneCount &&

                _skinningMatrixBuffer != null &&

                _skinningClipBuffer != null;

            if (alreadyUploaded)
            {
                return true;
            }

            ReleaseSkinningBuffers();

            _skinningMatrixBuffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    matrices.Length,
                    MatrixStrideBytes);

            _skinningMatrixBuffer.name =
                "Swarm RMI Skinning Matrices";

            _skinningMatrixBuffer.SetData(
                matrices);

            var clipInfos =
                new RmiSkinningClipInfo[
                    clips.Length];

            for (int i = 0;
                 i < clips.Length;
                 i++)
            {
                clipInfos[i] =
                    new RmiSkinningClipInfo
                    {
                        StartFrame =
                            clips[i].StartFrame,

                        FrameCount =
                            clips[i].FrameCount,

                        Length =
                            clips[i].Length,

                        Padding =
                            0f
                    };
            }

            _skinningClipBuffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    clipInfos.Length,
                    AnimationStrideBytes);

            _skinningClipBuffer.name =
                "Swarm RMI Skinning Clip Infos";

            _skinningClipBuffer.SetData(
                clipInfos);

            _uploadedClipSet =
                gpuSkinningClipSet;

            _uploadedMatrixCount =
                matrices.Length;

            _uploadedClipCount =
                clips.Length;

            _uploadedBoneCount =
                gpuSkinningClipSet
                    .BoneCount;

            return true;
        }

        private bool IsSkinningValid()
        {
            if (!enableGpuSkinning ||
                gpuSkinningClipSet == null ||
                !gpuSkinningClipSet.IsValid ||
                gpuSkinningClipSet.SourceMesh == null ||
                gpuSkinningClipSet.BoneCount <= 0 ||
                gpuSkinningClipSet.Clips == null ||
                gpuSkinningClipSet.Clips.Length == 0 ||
                gpuSkinningClipSet.BoneMatrices == null ||
                gpuSkinningClipSet.BoneMatrices.Length == 0)
            {
                return false;
            }

            int requiredFrames = 0;

            foreach (
                GpuSkinningClipSet.Clip clip
                in gpuSkinningClipSet.Clips)
            {
                if (clip.StartFrame < 0 ||
                    clip.FrameCount <= 0)
                {
                    return false;
                }

                requiredFrames =
                    math.max(
                        requiredFrames,
                        clip.StartFrame +
                        clip.FrameCount);
            }

            long requiredMatrices =
                (long)requiredFrames *
                gpuSkinningClipSet
                    .BoneCount;

            return requiredMatrices <=
                   gpuSkinningClipSet
                       .BoneMatrices.Length;
        }

        private Mesh GetRenderMesh()
        {
            return IsSkinningValid()
                ? gpuSkinningClipSet.SourceMesh
                : mesh;
        }

        /// <summary>
        /// 실제 메시의 서브메시 범위 안에서 유효하게 설정된 머터리얼 수를 센다.
        /// 측정 시작 전 통제 조건 검증과 프레임 제출 조건이 같은 기준을 사용한다.
        /// </summary>
        private int CountConfiguredMaterials(
            Mesh renderMesh)
        {
            if (renderMesh == null ||
                materials == null ||
                materials.Length == 0)
            {
                return 0;
            }

            int count =
                math.min(
                    renderMesh.subMeshCount,
                    materials.Length);

            int validCount = 0;

            for (int i = 0;
                 i < count;
                 i++)
            {
                if (materials[i] != null)
                {
                    validCount++;
                }
            }

            return validCount;
        }

        private bool HasUsableMaterials(
            Mesh renderMesh)
        {
            return CountConfiguredMaterials(
                       renderMesh) >
                   0;
        }

        private void UpdateStats(
            int instanceCount,
            Mesh renderMesh,
            int materialCount,
            int apiCallCount,
            int drawCommandCount,
            int commandsPerSubMesh)
        {
            if (!enableInstrumentation)
            {
                return;
            }

            int particleBytes =
                instanceCount *
                ParticleStrideBytes;

            int animationBytes =
                instanceCount *
                AnimationStrideBytes;

            int commandBytes =
                drawCommandCount *
                GraphicsBuffer
                    .IndirectDrawIndexedArgs
                    .size;

            _stats.ApplySubmissionMode(
                submissionMode);

            _stats.InstanceCount =
                instanceCount;

            _stats.InstanceCapacity =
                _instanceCapacity;

            _stats.MeshCount =
                renderMesh != null
                    ? 1
                    : 0;

            _stats.MaterialCount =
                materialCount;

            _stats.ApiCallCount =
                apiCallCount;

            _stats.DrawCommandCount =
                drawCommandCount;

            _stats.CommandsPerSubMesh =
                commandsPerSubMesh;

            _stats.ParticleUploadBytesPerFrame =
                particleBytes;

            _stats.AnimationUploadBytesPerFrame =
                animationBytes;

            _stats.IndirectArgsUploadBytesPerFrame =
                commandBytes;

            _stats.TotalUploadBytesPerFrame =
                particleBytes +
                animationBytes +
                commandBytes;

            _stats.MatrixBufferSizeBytes =
                _instanceCapacity *
                MatrixStrideBytes;

            _stats.FrameIndex =
                Time.frameCount;

            _stats.IsValid =
                true;
        }

        private void ReleaseResources()
        {
            ReleaseCommandBuffers();
            ReleaseInstanceBuffers();
            ReleaseSkinningBuffers();

            _queryReady = false;
            _world = null;
        }

        private void ReleaseCommandBuffers()
        {
            if (_commandBuffers != null)
            {
                foreach (
                    GraphicsBuffer buffer
                    in _commandBuffers)
                {
                    buffer?.Release();
                }
            }

            _commandBuffers = null;
            _commandData = null;

            _commandSubMeshCount = 0;
            _commandCapacity = 0;
        }

        private void ReleaseInstanceBuffers()
        {
            _matrixBuffer?.Release();
            _particleBuffer?.Release();
            _animationBuffer?.Release();

            _matrixBuffer = null;
            _particleBuffer = null;
            _animationBuffer = null;

            if (_particles.IsCreated)
            {
                _particles.Dispose();
            }

            if (_animations.IsCreated)
            {
                _animations.Dispose();
            }

            _instanceCapacity = 0;
        }

        private void ReleaseSkinningBuffers()
        {
            _skinningMatrixBuffer?.Release();
            _skinningClipBuffer?.Release();

            _skinningMatrixBuffer = null;
            _skinningClipBuffer = null;

            _uploadedClipSet = null;
            _uploadedMatrixCount = 0;
            _uploadedClipCount = 0;
            _uploadedBoneCount = 0;
        }

        [BurstCompile]
        private struct CollectDataJob :
            IJobChunk
        {
            [ReadOnly]
            public BufferTypeHandle<SoldierAgent>
                AgentHandle;

            [ReadOnly]
            public BufferTypeHandle<SoldierVariance>
                VarianceHandle;

            [ReadOnly]
            public BufferTypeHandle<SoldierCombatState>
                CombatHandle;

            [ReadOnly]
            public ComponentTypeHandle<UnitLocomotion>
                LocomotionHandle;

            [ReadOnly]
            public ComponentTypeHandle<UnitAnimationState>
                AnimationHandle;

            [ReadOnly]
            public ComponentTypeHandle<
                UnitAnimationReference>
                AnimationRefHandle;

            [ReadOnly]
            public ComponentTypeHandle<
                SwarmAnimationApproximationTuning>
                TuningHandle;

            [ReadOnly]
            public ComponentTypeHandle<DetectionTag>
                DetectionHandle;

            [ReadOnly]
            public ComponentTypeHandle<LocalTransform>
                TransformHandle;

            [ReadOnly]
            public NativeArray<int>
                ChunkOffsets;

            [ReadOnly]
            public float GlobalTime;

            [ReadOnly]
            public int SkinningClipCount;

            [NativeDisableParallelForRestriction]
            public NativeArray<RmiParticleData>
                OutParticles;

            [NativeDisableParallelForRestriction]
            public NativeArray<RmiAnimationPayload>
                OutAnimations;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                var agents =
                    chunk.GetBufferAccessor(
                        ref AgentHandle);

                var variances =
                    chunk.GetBufferAccessor(
                        ref VarianceHandle);

                var combats =
                    chunk.GetBufferAccessor(
                        ref CombatHandle);

                var locomotions =
                    chunk.GetNativeArray(
                        ref LocomotionHandle);

                var animationStates =
                    chunk.GetNativeArray(
                        ref AnimationHandle);

                var animationRefs =
                    chunk.GetNativeArray(
                        ref AnimationRefHandle);

                var tunings =
                    chunk.GetNativeArray(
                        ref TuningHandle);

                var detections =
                    chunk.GetNativeArray(
                        ref DetectionHandle);

                var transforms =
                    chunk.GetNativeArray(
                        ref TransformHandle);

                int outputIndex =
                    ChunkOffsets[
                        unfilteredChunkIndex];

                for (int entityIndex = 0;
                     entityIndex < chunk.Count;
                     entityIndex++)
                {
                    DynamicBuffer<SoldierAgent>
                        agentBuffer =
                            agents[entityIndex];

                    DynamicBuffer<SoldierVariance>
                        varianceBuffer =
                            variances[entityIndex];

                    DynamicBuffer<SoldierCombatState>
                        combatBuffer =
                            combats[entityIndex];

                    int count =
                        math.min(
                            agentBuffer.Length,
                            varianceBuffer.Length);

                    UnitLocomotion locomotion =
                        locomotions[entityIndex];

                    UnitAnimationState animationState =
                        animationStates[entityIndex];

                    UnitAnimationReference animationRef =
                        animationRefs[entityIndex];

                    SwarmAnimationApproximationTuning
                        tuning =
                            tunings[entityIndex];

                    DetectionTag detection =
                        detections[entityIndex];

                    float3 unitPosition =
                        transforms[entityIndex]
                            .Position;

                    for (int soldierIndex = 0;
                         soldierIndex < count;
                         soldierIndex++)
                    {
                        SoldierAgent agent =
                            agentBuffer[
                                soldierIndex];

                        SoldierVariance variance =
                            varianceBuffer[
                                soldierIndex];

                        SoldierCombatState combat =
                            soldierIndex <
                            combatBuffer.Length
                                ? combatBuffer[
                                    soldierIndex]
                                : default;

                        float agentSpeed =
                            math.length(
                                agent.Velocity);

                        LocomotionState visualState =
                            GetVisualState(
                                locomotion.State,
                                agentSpeed,
                                tuning);

                        float motionSpeed =
                            GetMotionSpeed(
                                animationRef
                                    .MotionSpeed,
                                agentSpeed,
                                visualState,
                                tuning);

                        float phaseSpeed =
                            math.max(
                                agent.AnimPhaseSpeed,
                                GetFallbackPhaseSpeed(
                                    visualState,
                                    motionSpeed));

                        float phase =
                            variance.Phase;

                        if (agent.AnimPhaseSpeed <=
                                0.01f &&
                            visualState !=
                                LocomotionState.Idle)
                        {
                            phase +=
                                GlobalTime *
                                phaseSpeed;
                        }

                        UnitAnimationClipId clip =
                            combat.InAttackRange
                                ? UnitAnimationClipId
                                    .Fight
                                : animationState
                                    .Clip;

                        OutParticles[outputIndex] =
                            new RmiParticleData
                            {
                                UnitPos =
                                    unitPosition,

                                LocalOffset =
                                    agent
                                        .CurrentLocalPos,

                                FacingYaw =
                                    agent.FacingYaw,

                                Phase =
                                    phase,

                                PhaseSpeed =
                                    phaseSpeed,

                                MotionScale =
                                    math.clamp(
                                        motionSpeed,
                                        0f,
                                        1f),

                                RenderScale =
                                    GetRenderScale(
                                        visualState,
                                        tuning)
                            };

                        OutAnimations[outputIndex] =
                            new RmiAnimationPayload
                            {
                                ClipIndex =
                                    GetSafeClipIndex(
                                        clip,
                                        SkinningClipCount),

                                NormalizedTime =
                                    GetNormalizedTime(
                                        animationState,
                                        clip,
                                        variance,
                                        combat),

                                PlaybackSpeed =
                                    GetPlaybackSpeed(
                                        animationState,
                                        clip,
                                        visualState,
                                        motionSpeed,
                                        combat),

                                Padding =
                                    detection.FactionId
                            };

                        outputIndex++;
                    }
                }
            }

            private static LocomotionState
                GetVisualState(
                    LocomotionState unitState,
                    float agentSpeed,
                    in SwarmAnimationApproximationTuning
                        tuning)
            {
                if (unitState !=
                    LocomotionState.Idle)
                {
                    return unitState;
                }

                float walk =
                    math.max(
                        0.001f,
                        tuning
                            .VisualWalkSpeedThreshold);

                float run =
                    math.max(
                        walk,
                        tuning
                            .VisualRunSpeedThreshold);

                if (agentSpeed > run)
                {
                    return LocomotionState.Run;
                }

                if (agentSpeed > walk)
                {
                    return LocomotionState.Walk;
                }

                return LocomotionState.Idle;
            }

            private static float GetMotionSpeed(
                float unitMotionSpeed,
                float agentSpeed,
                LocomotionState state,
                in SwarmAnimationApproximationTuning
                    tuning)
            {
                if (state ==
                    LocomotionState.Idle)
                {
                    return unitMotionSpeed;
                }

                float divisor =
                    math.max(
                        0.001f,
                        tuning
                            .VisualMotionSpeedDivisor);

                return math.max(
                    unitMotionSpeed,
                    math.saturate(
                        agentSpeed /
                        divisor));
            }

            private static float
                GetFallbackPhaseSpeed(
                    LocomotionState state,
                    float motionSpeed)
            {
                if (state ==
                    LocomotionState.Idle)
                {
                    return 0f;
                }

                if (state ==
                    LocomotionState.Walk)
                {
                    return math.max(
                        2.1f,
                        motionSpeed *
                        4.2f);
                }

                return math.max(
                    4.75f,
                    motionSpeed *
                    5.5f);
            }

            private static float GetNormalizedTime(
                in UnitAnimationState state,
                UnitAnimationClipId clip,
                in SoldierVariance variance,
                in SoldierCombatState combat)
            {
                if (clip ==
                        UnitAnimationClipId.Fight &&
                    combat.InAttackRange)
                {
                    float delay =
                        math.saturate(
                            variance
                                .AnimStartDelay);

                    float phaseOffset =
                        variance.Phase /
                        (2f * math.PI);

                    float value =
                        math.frac(
                            combat.AttackTimer +
                            phaseOffset);

                    return math.saturate(
                        (value - delay) /
                        math.max(
                            0.0001f,
                            1f - delay));
                }

                if (state.Mode ==
                    UnitAnimationMode.OneShot)
                {
                    float delay =
                        math.saturate(
                            variance
                                .AnimStartDelay);

                    return math.saturate(
                        (
                            state.NormalizedTime -
                            delay
                        ) /
                        math.max(
                            0.0001f,
                            1f - delay));
                }

                return math.frac(
                    state.NormalizedTime +
                    variance.Phase /
                    (2f * math.PI));
            }

            private static float GetPlaybackSpeed(
                in UnitAnimationState state,
                UnitAnimationClipId clip,
                LocomotionState visualState,
                float motionSpeed,
                in SoldierCombatState combat)
            {
                if (clip ==
                        UnitAnimationClipId.Fight &&
                    combat.InAttackRange)
                {
                    return 1f;
                }

                if (state.PlaybackSpeed > 0f)
                {
                    return state.PlaybackSpeed;
                }

                if (visualState ==
                    LocomotionState.Idle)
                {
                    return 0.2f;
                }

                if (visualState ==
                    LocomotionState.Walk)
                {
                    return math.lerp(
                        0.6f,
                        1.1f,
                        math.saturate(
                            motionSpeed));
                }

                return math.lerp(
                    1f,
                    1.6f,
                    math.saturate(
                        motionSpeed));
            }

            private static int GetSafeClipIndex(
                UnitAnimationClipId clip,
                int clipCount)
            {
                int index =
                    clip switch
                    {
                        UnitAnimationClipId.Walk =>
                            1,

                        UnitAnimationClipId.Run =>
                            2,

                        UnitAnimationClipId.Fight =>
                            3,

                        _ =>
                            0
                    };

                if (clipCount <= 0)
                {
                    return math.max(
                        0,
                        index);
                }

                if (index >= 0 &&
                    index < clipCount)
                {
                    return index;
                }

                return clip ==
                       UnitAnimationClipId.Fight
                    ? 0
                    : math.clamp(
                        index,
                        0,
                        clipCount - 1);
            }

            private static float GetRenderScale(
                LocomotionState state,
                in SwarmAnimationApproximationTuning
                    tuning)
            {
                if (state ==
                    LocomotionState.Walk)
                {
                    return tuning
                        .WalkRenderScale;
                }

                if (state ==
                    LocomotionState.Run)
                {
                    return tuning
                        .RunRenderScale;
                }

                return tuning
                    .IdleRenderScale;
            }
        }
    }
}
