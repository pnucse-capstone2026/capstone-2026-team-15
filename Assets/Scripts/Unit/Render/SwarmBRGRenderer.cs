using System;
using System.Collections.Generic;
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
    /// <summary>
    /// 동일한 ECS 시뮬레이션 결과를 BatchRendererGroup으로 제출하는 렌더러.
    ///
    /// 최종 2x2 실험에서 이 컴포넌트가 담당하는 조건:
    /// - BRG / Integrated
    /// - BRG / Split
    ///
    /// Render-only 격자 데이터 경로는 제거했다.
    /// 모든 인스턴스 데이터는 ECS 시뮬레이션 결과에서 생성한다.
    ///
    /// Materials 배열의 각 항목은 대응하는 submesh의 실제 렌더 material이다.
    /// 별도의 단일 Material fallback은 사용하지 않는다.
    /// 런타임에는 source material 전체를 복제하여 GPU skinning buffer를 안전하게 바인딩한다.
    /// </summary>
    public sealed class SwarmBRGRenderer :
        MonoBehaviour,
        ISwarmRendererInstrumentationSource
    {
        // 2x2 실험에서 두 백엔드에 공통 적용할 고정 렌더 상태.
        private const int ForcedMeshLod = 0;
        private const uint AllRenderingLayers = uint.MaxValue;

        // ==========================================================
        // Inspector Settings
        // ==========================================================

        [Header("Assets")]
        [Tooltip("GPU skinning을 사용하지 않을 때 사용할 mesh. GPU skinning 사용 시 ClipSet.SourceMesh를 사용한다.")]
        [SerializeField] private Mesh mesh;

        [Tooltip("submesh 순서와 동일하게 배치할 BRG/DOTS Instancing material 배열. 단일 Material fallback은 사용하지 않는다.")]
        [SerializeField] private Material[] materials;

        [Header("BRG Submission")]
        [SerializeField]
        private SwarmSubmissionMode submissionMode =
            SwarmSubmissionMode.Integrated;

        [Tooltip("Split 조건에서 submesh당 생성할 BatchDrawCommand 수.")]
        [Min(1)]
        [SerializeField] private int splitDrawCommandCount = 4;

        [Header("Capacity")]
        [Tooltip("최초 BRG instance buffer capacity. 실험 최대 인스턴스 수 이상으로 설정하면 측정 중 재할당을 피할 수 있다.")]
        [Min(1)]
        [SerializeField] private int initialInstanceCapacity = 4096;

        [Header("ECS Visual")]
        [Min(0.001f)]
        [SerializeField] private float particleScale = 1.0f;

        [SerializeField] private float yOffset = 0.0f;

        [Header("GPU Skinning")]
        [SerializeField] private bool enableGpuSkinning = true;

        [SerializeField] private GpuSkinningClipSet gpuSkinningClipSet;

        [Header("Bounds")]
        [SerializeField] private Vector3 boundsCenter = Vector3.zero;

        [SerializeField] private Vector3 boundsSize = Vector3.one * 10000f;

        [Header("Instrumentation")]
        [Tooltip("BenchmarkRunner가 CurrentStats를 읽을 수 있도록 구조 계측을 활성화합니다.")]
        [SerializeField] private bool enableInstrumentation = true;

        [Header("Debug")]
        [SerializeField] private bool logRuntimeMaterialBinding = false;

        // ==========================================================
        // Shader Property IDs / Keywords
        // ==========================================================

        private const string GpuSkinningKeyword =
            "SWARM_GPU_SKINNING_ON";

        private static readonly int YOffsetPropertyID =
            Shader.PropertyToID("_YOffset");

        private static readonly int SkinningMatricesPropertyID =
            Shader.PropertyToID("_SkinningMatrices");

        private static readonly int SkinningClipInfosPropertyID =
            Shader.PropertyToID("_SkinningClipInfos");

        private static readonly int SkinningEnabledPropertyID =
            Shader.PropertyToID("_SkinningEnabled");

        private static readonly int SkinningBoneCountPropertyID =
            Shader.PropertyToID("_SkinningBoneCount");

        private static readonly int SkinningClipCountPropertyID =
            Shader.PropertyToID("_SkinningClipCount");

        // ==========================================================
        // GPU Skinning Data Layout
        // ==========================================================

        private struct SwarmBrgSkinningClipInfo
        {
            public int StartFrame;
            public int FrameCount;
            public float Length;
            public float Padding;
        }

        // ==========================================================
        // BRG Resources
        // ==========================================================

        private BatchRendererGroup _brg;
        private GraphicsBuffer _instanceDataBuffer;

        private BatchID _batchID;
        private BatchMeshID _meshID;
        private BatchMaterialID[] _materialIDs;

        private Mesh _registeredMesh;
        private Material[] _registeredSourceMaterials;
        private Material[] _runtimeMaterials;
        private int[] _drawUnitSubMeshIndices;

        private bool _initialized;
        private bool _hasBatch;
        private bool _registeredGpuSkinningActive;
        private bool _runtimeBindingLogged;
        private bool _rebuildRequested;
        private bool _materialBindingDirty;

        private float _appliedYOffset = float.NaN;

        private int _activeInstanceCount;
        private int _activeInstanceCapacity;

        private SwarmBrgInstanceBufferLayout _layout;

        // ==========================================================
        // Persistent Instance Data
        // ==========================================================

        private NativeArray<float4> _positionYawArray;
        private NativeArray<float4> _offsetScaleArray;
        private NativeArray<float4> _animationArray;
        private float4[] _zeroArray;

        // ==========================================================
        // GPU Skinning Buffers
        // ==========================================================

        private GraphicsBuffer _skinningMatrixBuffer;
        private GraphicsBuffer _skinningClipInfoBuffer;
        private GpuSkinningClipSet _uploadedSkinningClipSet;

        // ==========================================================
        // ECS Query
        // ==========================================================

        private World _world;
        private EntityManager _entityManager;
        private EntityQuery _ecsQuery;
        private bool _queryReady;

        // ==========================================================
        // Stats
        // ==========================================================

        private SwarmBRGStats _stats;

        public SwarmBRGStats CurrentStats => _stats;

        public bool InstrumentationEnabled =>
            enableInstrumentation;

        public SwarmRenderPipelineCase InstrumentationCase =>
            SwarmRenderPipelineCaseUtility.GetCase(
                SwarmRenderBackend.BatchRendererGroup,
                submissionMode);

        // ==========================================================
        // Unity Lifecycle
        // ==========================================================

        private void OnEnable()
        {
            _stats = SwarmBRGStats.CreateDefault();
            InitializeRenderer();
        }

        private void OnDisable()
        {
            ReleaseRenderer();
        }

        private void OnDestroy()
        {
            ReleaseRenderer();
        }

        private void OnValidate()
        {
            initialInstanceCapacity =
                math.max(1, initialInstanceCapacity);

            splitDrawCommandCount =
                math.max(1, splitDrawCommandCount);

            particleScale =
                math.max(0.001f, particleScale);

            boundsSize.x =
                math.max(0.001f, boundsSize.x);

            boundsSize.y =
                math.max(0.001f, boundsSize.y);

            boundsSize.z =
                math.max(0.001f, boundsSize.z);

            _rebuildRequested = true;
            _materialBindingDirty = true;
        }

        private void Update()
        {
            if (!_initialized)
            {
                InitializeRenderer();
            }

            if (_initialized && ShouldRebuildRenderer())
            {
                RebuildRenderer(
                    math.max(
                        1,
                        math.max(
                            _activeInstanceCapacity,
                            initialInstanceCapacity)));
            }

            if (!_initialized)
            {
                MarkInstrumentationInvalid();
                return;
            }

            RefreshRuntimeMaterialBindingsIfNeeded();

            int instanceCount =
                BuildEcsInstanceData();

            if (instanceCount <= 0)
            {
                _activeInstanceCount = 0;
                UpdateStatsForEmptyFrame();
                return;
            }

            UploadInstanceData(instanceCount);
            _activeInstanceCount = instanceCount;

            if (enableInstrumentation)
            {
                int drawCommandCount =
                    GetActiveDrawCommandCount(instanceCount);

                _stats.ApplySubmissionMode(submissionMode);

                _stats.SetInstanceInfo(
                    instanceCount,
                    _activeInstanceCapacity,
                    _layout.TotalSizeBytes);

                _stats.SetResourceInfo(
                    _registeredMesh != null ? 1 : 0,
                    GetActiveDrawUnitCount());

                _stats.SetSubmissionInfo(
                    batchCount: _hasBatch ? 1 : 0,
                    drawRangeCount: drawCommandCount > 0 ? 1 : 0,
                    drawCommandCount: drawCommandCount);

                _stats.MarkValid(Time.frameCount);
            }
        }

        // ==========================================================
        // Public Control
        // ==========================================================

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

            int activeMaterialCount =
                _stats.MaterialCount;

            int commandsPerSubMesh =
                activeMaterialCount > 0
                    ? _stats.DrawCommandCount /
                      activeMaterialCount
                    : 0;

            sample = new SwarmRendererInstrumentationSample
            {
                IsValid = true,
                FrameIndex = _stats.FrameIndex,
                RenderCase = _stats.RenderCase,
                SubmissionMode = _stats.SubmissionMode,
                InstanceCount = _stats.InstanceCount,
                ActiveSubMeshCount = activeMaterialCount,
                ActiveMaterialCount = activeMaterialCount,
                CommandsPerSubMesh = commandsPerSubMesh,
                SubmittedCommandCount =
                    _stats.DrawCommandCount,

                // BRG는 RenderMeshIndirect처럼 호출 횟수를 직접 세지 않는다.
                ApiCallCount = 0,
                UploadedBytesPerFrame =
                    _stats.UploadedBytesPerFrame,
                ComputeDispatchCount = 0,
                BatchCount = _stats.BatchCount,
                DrawRangeCount = _stats.DrawRangeCount,
                BatchDrawCommandCount =
                    _stats.DrawCommandCount
            };

            return true;
        }

        /// <summary>
        /// BenchmarkRunner가 측정 시작 전에 두 렌더러의 실험 통제 조건을
        /// 비교할 수 있도록 현재 구성값을 반환한다.
        /// 이 메서드는 배치 생성, GPU 업로드 또는 ECS 순회를 수행하지 않는다.
        /// </summary>
        public SwarmRendererControlState GetControlState()
        {
            bool gpuSkinningActive =
                IsGpuSkinningRequestedAndValid();

            Mesh renderMesh =
                GetRenderMesh(
                    gpuSkinningActive);

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

        public void SetSubmissionMode(
            SwarmSubmissionMode nextMode)
        {
            submissionMode = nextMode;
        }

        /// <summary>
        /// 기존 BenchmarkRunner와의 호환용 진입점.
        /// 이 렌더러는 BRG 조건만 담당하므로 BRG case의 제출 방식만 반영한다.
        /// RenderMeshIndirect case는 SwarmRenderMeshIndirectRenderer가 담당한다.
        /// </summary>
        public void SetRenderPipelineCase(
            SwarmRenderPipelineCase nextCase)
        {
            if (!SwarmRenderPipelineCaseUtility.IsBrg(nextCase))
            {
                Debug.LogWarning(
                    $"SwarmBRGRenderer: {nextCase}는 BRG 조건이 아닙니다. " +
                    "RenderMeshIndirect 조건은 SwarmRenderMeshIndirectRenderer에 적용해야 합니다.",
                    this);

                return;
            }

            SetSubmissionMode(
                SwarmRenderPipelineCaseUtility.GetSubmissionMode(nextCase));
        }

        // ==========================================================
        // Initialization / Rebuild
        // ==========================================================

        private void InitializeRenderer(
            int minimumCapacity = 0)
        {
            bool gpuSkinningActive =
                IsGpuSkinningRequestedAndValid();

            Mesh renderMesh =
                GetRenderMesh(gpuSkinningActive);

            if (renderMesh == null)
            {
                _initialized = false;
                return;
            }

            if (!HasValidMaterialConfiguration(renderMesh))
            {
                Debug.LogWarning(
                    "SwarmBRGRenderer: Materials 배열에 submesh 순서와 대응하는 material을 할당해야 합니다. " +
                    "단일 Material fallback은 사용하지 않습니다.");

                _initialized = false;
                return;
            }

            if (BatchRendererGroup.BufferTarget ==
                BatchBufferTarget.UnsupportedByUnderlyingGraphicsApi)
            {
                Debug.LogError(
                    "SwarmBRGRenderer: 현재 Graphics API가 BatchRendererGroup buffer target을 지원하지 않습니다.");

                _initialized = false;
                return;
            }

            _registeredGpuSkinningActive =
                gpuSkinningActive;

            _registeredMesh =
                renderMesh;

            if (!CreateRuntimeMaterials(renderMesh))
            {
                _initialized = false;
                return;
            }

            PrepareRuntimeMaterialsForRegistration();

            int requestedCapacity =
                math.max(
                    1,
                    math.max(
                        initialInstanceCapacity,
                        minimumCapacity));

            int capacity =
                Mathf.NextPowerOfTwo(requestedCapacity);

            _brg =
                new BatchRendererGroup(
                    OnPerformCulling,
                    IntPtr.Zero);

            _brg.SetGlobalBounds(
                new Bounds(
                    boundsCenter,
                    boundsSize));

            _meshID =
                _brg.RegisterMesh(_registeredMesh);

            _materialIDs =
                new BatchMaterialID[_runtimeMaterials.Length];

            for (int i = 0; i < _runtimeMaterials.Length; i++)
            {
                _materialIDs[i] =
                    _brg.RegisterMaterial(_runtimeMaterials[i]);
            }

            CreateBatchAndBuffers(capacity);

            _initialized = true;
            _rebuildRequested = false;
            _materialBindingDirty = false;

            LogRuntimeMaterialBindingIfNeeded();
        }

        private void RebuildRenderer(
            int requiredInstanceCount)
        {
            ReleaseRenderer();
            InitializeRenderer(requiredInstanceCount);
        }

        private bool ShouldRebuildRenderer()
        {
            if (_rebuildRequested)
            {
                return true;
            }

            bool desiredGpuSkinningActive =
                IsGpuSkinningRequestedAndValid();

            Mesh desiredMesh =
                GetRenderMesh(desiredGpuSkinningActive);

            if (desiredGpuSkinningActive !=
                _registeredGpuSkinningActive)
            {
                return true;
            }

            if (_registeredMesh != desiredMesh)
            {
                return true;
            }

            if (!AreRegisteredSourceMaterialsCurrent(desiredMesh))
            {
                return true;
            }

            if (_runtimeMaterials == null ||
                _materialIDs == null ||
                _drawUnitSubMeshIndices == null ||
                _runtimeMaterials.Length == 0 ||
                _runtimeMaterials.Length != _materialIDs.Length ||
                _runtimeMaterials.Length != _drawUnitSubMeshIndices.Length)
            {
                return true;
            }

            return false;
        }

        private Mesh GetRenderMesh(
            bool gpuSkinningActive)
        {
            if (gpuSkinningActive)
            {
                return gpuSkinningClipSet.SourceMesh;
            }

            return mesh;
        }

        private bool IsGpuSkinningRequestedAndValid()
        {
            if (!enableGpuSkinning ||
                gpuSkinningClipSet == null ||
                !gpuSkinningClipSet.IsValid ||
                gpuSkinningClipSet.SourceMesh == null ||
                gpuSkinningClipSet.BoneCount <= 0)
            {
                return false;
            }

            Matrix4x4[] matrices =
                gpuSkinningClipSet.BoneMatrices;

            GpuSkinningClipSet.Clip[] clips =
                gpuSkinningClipSet.Clips;

            if (matrices == null ||
                matrices.Length == 0 ||
                clips == null ||
                clips.Length == 0)
            {
                return false;
            }

            int boneCount =
                gpuSkinningClipSet.BoneCount;

            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i].StartFrame < 0 ||
                    clips[i].FrameCount <= 0 ||
                    clips[i].Length <= 0f)
                {
                    return false;
                }

                long requiredMatrixCount =
                    (long)(clips[i].StartFrame + clips[i].FrameCount) *
                    boneCount;

                if (requiredMatrixCount > matrices.Length)
                {
                    return false;
                }
            }

            return true;
        }

        // ==========================================================
        // Materials / Draw Units
        // ==========================================================

        /// <summary>
        /// 실제 메시의 서브메시 범위 안에서 유효하게 설정된 머터리얼 수를 센다.
        /// 런타임 머터리얼 생성 전에도 동일성 검증이 가능하도록 원본 배열을 사용한다.
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

        private bool HasValidMaterialConfiguration(
            Mesh renderMesh)
        {
            return CountConfiguredMaterials(
                       renderMesh) >
                   0;
        }

        private bool CreateRuntimeMaterials(
            Mesh renderMesh)
        {
            ReleaseRuntimeMaterials();

            if (!HasValidMaterialConfiguration(renderMesh))
            {
                return false;
            }

            int searchCount =
                math.min(
                    renderMesh.subMeshCount,
                    materials.Length);

            var runtimeMaterialList =
                new List<Material>(searchCount);

            var sourceMaterialList =
                new List<Material>(searchCount);

            var subMeshIndexList =
                new List<int>(searchCount);

            for (int subMeshIndex = 0;
                 subMeshIndex < searchCount;
                 subMeshIndex++)
            {
                Material sourceMaterial =
                    materials[subMeshIndex];

                if (sourceMaterial == null)
                {
                    continue;
                }

                Material runtimeMaterial =
                    new Material(sourceMaterial)
                    {
                        name =
                            sourceMaterial.name +
                            " (Runtime BRG SubMesh " +
                            subMeshIndex +
                            ")",

                        hideFlags =
                            HideFlags.DontSaveInEditor |
                            HideFlags.DontSaveInBuild
                    };

                runtimeMaterial.enableInstancing = true;

                ClearSkinningMaterialState(runtimeMaterial);
                ApplyGpuSkinningKeyword(
                    runtimeMaterial,
                    _registeredGpuSkinningActive);

                runtimeMaterial.SetFloat(
                    YOffsetPropertyID,
                    yOffset);

                runtimeMaterialList.Add(runtimeMaterial);
                sourceMaterialList.Add(sourceMaterial);
                subMeshIndexList.Add(subMeshIndex);
            }

            if (runtimeMaterialList.Count <= 0)
            {
                return false;
            }

            _runtimeMaterials =
                runtimeMaterialList.ToArray();

            _registeredSourceMaterials =
                sourceMaterialList.ToArray();

            _drawUnitSubMeshIndices =
                subMeshIndexList.ToArray();

            _runtimeBindingLogged = false;
            _materialBindingDirty = true;
            _appliedYOffset = float.NaN;

            return true;
        }

        private bool AreRegisteredSourceMaterialsCurrent(
            Mesh renderMesh)
        {
            if (renderMesh == null ||
                materials == null ||
                _registeredSourceMaterials == null ||
                _drawUnitSubMeshIndices == null)
            {
                return false;
            }

            int searchCount =
                math.min(
                    renderMesh.subMeshCount,
                    materials.Length);

            int registeredIndex = 0;

            for (int subMeshIndex = 0;
                 subMeshIndex < searchCount;
                 subMeshIndex++)
            {
                Material sourceMaterial =
                    materials[subMeshIndex];

                if (sourceMaterial == null)
                {
                    continue;
                }

                if (registeredIndex >=
                    _registeredSourceMaterials.Length)
                {
                    return false;
                }

                if (_registeredSourceMaterials[registeredIndex] !=
                    sourceMaterial)
                {
                    return false;
                }

                if (_drawUnitSubMeshIndices[registeredIndex] !=
                    subMeshIndex)
                {
                    return false;
                }

                registeredIndex++;
            }

            return registeredIndex ==
                   _registeredSourceMaterials.Length;
        }

        private int GetActiveDrawUnitCount()
        {
            return _drawUnitSubMeshIndices != null
                ? _drawUnitSubMeshIndices.Length
                : 0;
        }

        private void ReleaseRuntimeMaterials()
        {
            if (_runtimeMaterials != null)
            {
                for (int i = 0; i < _runtimeMaterials.Length; i++)
                {
                    Material runtimeMaterial =
                        _runtimeMaterials[i];

                    if (runtimeMaterial == null)
                    {
                        continue;
                    }

                    ClearSkinningMaterialState(runtimeMaterial);

                    if (Application.isPlaying)
                    {
                        Destroy(runtimeMaterial);
                    }
                    else
                    {
                        DestroyImmediate(runtimeMaterial);
                    }
                }
            }

            _runtimeMaterials = null;
            _registeredSourceMaterials = null;
            _drawUnitSubMeshIndices = null;
            _materialIDs = null;
        }

        // ==========================================================
        // BRG Batch / Instance Buffer
        // ==========================================================

        private void CreateBatchAndBuffers(
            int instanceCapacity)
        {
            _activeInstanceCapacity =
                math.max(1, instanceCapacity);

            _layout =
                SwarmBrgInstanceProperties.CalculateLayout(
                    _activeInstanceCapacity);

            CreateInstanceDataBuffer();
            CreatePersistentInstanceArrays();

            int reservedFloat4Count =
                math.max(
                    1,
                    SwarmBrgInstanceProperties.ReservedBytes /
                    SwarmBrgInstanceProperties.Float4SizeBytes);

            _zeroArray =
                new float4[reservedFloat4Count];

            MetadataValue[] metadataValues =
                SwarmBrgInstanceProperties.CreateMetadata(_layout);

            using var metadata =
                new NativeArray<MetadataValue>(
                    metadataValues,
                    Allocator.Temp);

            _batchID =
                _brg.AddBatch(
                    metadata,
                    _instanceDataBuffer.bufferHandle);

            _hasBatch = true;

            UploadZeroBlock();
        }

        private void CreateInstanceDataBuffer()
        {
            if (BatchRendererGroup.BufferTarget ==
                BatchBufferTarget.ConstantBuffer)
            {
                Debug.LogWarning(
                    "SwarmBRGRenderer: 현재 플랫폼은 ConstantBuffer BRG 경로입니다. " +
                    "이 구현은 RawBuffer 지원 환경을 기준으로 검증했습니다.");
            }

            _instanceDataBuffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Raw,
                    _layout.TotalRawIntCount,
                    SwarmBrgInstanceProperties.RawIntSizeBytes);
        }

        private void CreatePersistentInstanceArrays()
        {
            DisposePersistentInstanceArrays();

            _positionYawArray =
                new NativeArray<float4>(
                    _activeInstanceCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);

            _offsetScaleArray =
                new NativeArray<float4>(
                    _activeInstanceCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);

            _animationArray =
                new NativeArray<float4>(
                    _activeInstanceCapacity,
                    Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
        }

        private void DisposePersistentInstanceArrays()
        {
            if (_positionYawArray.IsCreated)
            {
                _positionYawArray.Dispose();
            }

            if (_offsetScaleArray.IsCreated)
            {
                _offsetScaleArray.Dispose();
            }

            if (_animationArray.IsCreated)
            {
                _animationArray.Dispose();
            }
        }

        private void UploadZeroBlock()
        {
            if (_instanceDataBuffer == null ||
                _zeroArray == null ||
                _zeroArray.Length == 0)
            {
                return;
            }

            _instanceDataBuffer.SetData(
                _zeroArray,
                0,
                0,
                _zeroArray.Length);
        }

        // ==========================================================
        // Material / Skinning Binding
        // ==========================================================

        private void PrepareRuntimeMaterialsForRegistration()
        {
            _materialBindingDirty = true;
            RefreshRuntimeMaterialBindingsIfNeeded();
        }

        private void RefreshRuntimeMaterialBindingsIfNeeded()
        {
            if (_runtimeMaterials == null ||
                _runtimeMaterials.Length == 0)
            {
                return;
            }

            bool skinningUploadRequired =
                _registeredGpuSkinningActive &&
                (_uploadedSkinningClipSet != gpuSkinningClipSet ||
                 _skinningMatrixBuffer == null ||
                 _skinningClipInfoBuffer == null);

            bool yOffsetChanged =
                float.IsNaN(_appliedYOffset) ||
                !Mathf.Approximately(_appliedYOffset, yOffset);

            if (!_materialBindingDirty &&
                !skinningUploadRequired &&
                !yOffsetChanged)
            {
                return;
            }

            for (int i = 0; i < _runtimeMaterials.Length; i++)
            {
                Material runtimeMaterial =
                    _runtimeMaterials[i];

                if (runtimeMaterial == null)
                {
                    continue;
                }

                runtimeMaterial.enableInstancing = true;
                runtimeMaterial.SetFloat(
                    YOffsetPropertyID,
                    yOffset);
            }

            if (!_registeredGpuSkinningActive)
            {
                for (int i = 0; i < _runtimeMaterials.Length; i++)
                {
                    ClearSkinningMaterialState(
                        _runtimeMaterials[i]);
                }

                _appliedYOffset = yOffset;
                _materialBindingDirty = false;
                return;
            }

            if (!UploadSkinningDataIfNeeded())
            {
                for (int i = 0; i < _runtimeMaterials.Length; i++)
                {
                    ClearSkinningMaterialState(
                        _runtimeMaterials[i]);
                }

                Debug.LogWarning(
                    "SwarmBRGRenderer: GPU skinning buffer upload에 실패했습니다. " +
                    "GpuSkinningClipSet 설정을 확인하세요.");

                _appliedYOffset = yOffset;
                _materialBindingDirty = false;
                return;
            }

            BindSkinningBuffersToAllRuntimeMaterials();

            _appliedYOffset = yOffset;
            _materialBindingDirty = false;

            LogRuntimeMaterialBindingIfNeeded();
        }

        private void ApplyGpuSkinningKeyword(
            Material targetMaterial,
            bool gpuSkinningActive)
        {
            if (targetMaterial == null)
            {
                return;
            }

            if (gpuSkinningActive)
            {
                targetMaterial.EnableKeyword(GpuSkinningKeyword);
            }
            else
            {
                targetMaterial.DisableKeyword(GpuSkinningKeyword);
            }
        }

        private void ClearSkinningMaterialState(
            Material targetMaterial)
        {
            if (targetMaterial == null)
            {
                return;
            }

            targetMaterial.DisableKeyword(GpuSkinningKeyword);

            targetMaterial.SetFloat(
                SkinningEnabledPropertyID,
                0f);

            targetMaterial.SetFloat(
                SkinningBoneCountPropertyID,
                1f);

            targetMaterial.SetFloat(
                SkinningClipCountPropertyID,
                1f);
        }

        private void BindSkinningBuffersToAllRuntimeMaterials()
        {
            int boneCount =
                gpuSkinningClipSet != null
                    ? gpuSkinningClipSet.BoneCount
                    : 1;

            int clipCount =
                gpuSkinningClipSet != null &&
                gpuSkinningClipSet.Clips != null
                    ? gpuSkinningClipSet.Clips.Length
                    : 1;

            for (int i = 0; i < _runtimeMaterials.Length; i++)
            {
                BindSkinningBuffers(
                    _runtimeMaterials[i],
                    boneCount,
                    clipCount);
            }
        }

        private void BindSkinningBuffers(
            Material targetMaterial,
            int boneCount,
            int clipCount)
        {
            if (targetMaterial == null ||
                _skinningMatrixBuffer == null ||
                _skinningClipInfoBuffer == null)
            {
                return;
            }

            ApplyGpuSkinningKeyword(
                targetMaterial,
                true);

            targetMaterial.SetBuffer(
                SkinningMatricesPropertyID,
                _skinningMatrixBuffer);

            targetMaterial.SetBuffer(
                SkinningClipInfosPropertyID,
                _skinningClipInfoBuffer);

            targetMaterial.SetFloat(
                SkinningEnabledPropertyID,
                1f);

            targetMaterial.SetFloat(
                SkinningBoneCountPropertyID,
                math.max(1, boneCount));

            targetMaterial.SetFloat(
                SkinningClipCountPropertyID,
                math.max(1, clipCount));
        }

        private bool UploadSkinningDataIfNeeded()
        {
            if (!_registeredGpuSkinningActive ||
                !IsGpuSkinningRequestedAndValid())
            {
                ReleaseSkinningBuffers();
                return false;
            }

            if (_uploadedSkinningClipSet == gpuSkinningClipSet &&
                _skinningMatrixBuffer != null &&
                _skinningClipInfoBuffer != null)
            {
                return true;
            }

            ReleaseSkinningBuffers();

            Matrix4x4[] boneMatrices =
                gpuSkinningClipSet.BoneMatrices;

            _skinningMatrixBuffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    boneMatrices.Length,
                    64);

            _skinningMatrixBuffer.SetData(boneMatrices);

            GpuSkinningClipSet.Clip[] clips =
                gpuSkinningClipSet.Clips;

            var clipInfos =
                new SwarmBrgSkinningClipInfo[clips.Length];

            for (int i = 0; i < clips.Length; i++)
            {
                clipInfos[i] =
                    new SwarmBrgSkinningClipInfo
                    {
                        StartFrame = clips[i].StartFrame,
                        FrameCount = clips[i].FrameCount,
                        Length = clips[i].Length,
                        Padding = 0f
                    };
            }

            _skinningClipInfoBuffer =
                new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    clipInfos.Length,
                    16);

            _skinningClipInfoBuffer.SetData(clipInfos);

            _uploadedSkinningClipSet =
                gpuSkinningClipSet;

            _runtimeBindingLogged = false;

            return true;
        }

        private void ReleaseSkinningBuffers()
        {
            if (_skinningMatrixBuffer != null)
            {
                _skinningMatrixBuffer.Release();
                _skinningMatrixBuffer = null;
            }

            if (_skinningClipInfoBuffer != null)
            {
                _skinningClipInfoBuffer.Release();
                _skinningClipInfoBuffer = null;
            }

            _uploadedSkinningClipSet = null;
            _runtimeBindingLogged = false;
        }

        private void LogRuntimeMaterialBindingIfNeeded()
        {
            if (!logRuntimeMaterialBinding ||
                _runtimeBindingLogged ||
                _runtimeMaterials == null)
            {
                return;
            }

            int matrixCount =
                _skinningMatrixBuffer != null
                    ? _skinningMatrixBuffer.count
                    : 0;

            int clipInfoCount =
                _skinningClipInfoBuffer != null
                    ? _skinningClipInfoBuffer.count
                    : 0;

            int boneCount =
                gpuSkinningClipSet != null
                    ? gpuSkinningClipSet.BoneCount
                    : 0;

            int clipCount =
                gpuSkinningClipSet != null &&
                gpuSkinningClipSet.Clips != null
                    ? gpuSkinningClipSet.Clips.Length
                    : 0;

            for (int i = 0; i < _runtimeMaterials.Length; i++)
            {
                Material runtimeMaterial =
                    _runtimeMaterials[i];

                if (runtimeMaterial == null)
                {
                    continue;
                }

                string subMeshText =
                    _drawUnitSubMeshIndices != null &&
                    i < _drawUnitSubMeshIndices.Length
                        ? _drawUnitSubMeshIndices[i].ToString()
                        : "?";

                Debug.Log(
                    "SwarmBRGRenderer Runtime Material - " +
                    "DrawUnit=" + i +
                    ", SubMesh=" + subMeshText +
                    ", SourceMaterial=" + _registeredSourceMaterials[i].name +
                    ", RuntimeMaterial=" + runtimeMaterial.name +
                    ", Shader=" + runtimeMaterial.shader.name +
                    ", Instancing=" + runtimeMaterial.enableInstancing +
                    ", Keyword=" + runtimeMaterial.IsKeywordEnabled(GpuSkinningKeyword) +
                    ", SkinningActive=" + _registeredGpuSkinningActive +
                    ", BoneCount=" + boneCount +
                    ", ClipCount=" + clipCount +
                    ", MatrixBufferCount=" + matrixCount +
                    ", ClipInfoBufferCount=" + clipInfoCount);
            }

            _runtimeBindingLogged = true;
        }

        // ==========================================================
        // ECS Data Collection
        // ==========================================================

        private int BuildEcsInstanceData()
        {
            if (!EnsureEcsQuery() ||
                _ecsQuery.IsEmpty)
            {
                return 0;
            }

            _ecsQuery.CompleteDependency();

            NativeArray<ArchetypeChunk> chunks = default;
            NativeArray<int> chunkOffsets = default;

            try
            {
                chunks =
                    _ecsQuery.ToArchetypeChunkArray(
                        Allocator.TempJob);

                if (!chunks.IsCreated ||
                    chunks.Length == 0)
                {
                    return 0;
                }

                var agentHandle =
                    _entityManager.GetBufferTypeHandle<SoldierAgent>(true);

                var varianceHandle =
                    _entityManager.GetBufferTypeHandle<SoldierVariance>(true);

                var combatHandle =
                    _entityManager.GetBufferTypeHandle<SoldierCombatState>(true);

                var locomotionHandle =
                    _entityManager.GetComponentTypeHandle<UnitLocomotion>(true);

                var animationStateHandle =
                    _entityManager.GetComponentTypeHandle<UnitAnimationState>(true);

                var animationReferenceHandle =
                    _entityManager.GetComponentTypeHandle<UnitAnimationReference>(true);

                var animationTuningHandle =
                    _entityManager.GetComponentTypeHandle<SwarmAnimationApproximationTuning>(true);

                var detectionHandle =
                    _entityManager.GetComponentTypeHandle<DetectionTag>(true);

                var localTransformHandle =
                    _entityManager.GetComponentTypeHandle<LocalTransform>(true);

                chunkOffsets =
                    new NativeArray<int>(
                        chunks.Length,
                        Allocator.TempJob,
                        NativeArrayOptions.UninitializedMemory);

                int totalSoldiers = 0;

                for (int chunkIndex = 0;
                     chunkIndex < chunks.Length;
                     chunkIndex++)
                {
                    chunkOffsets[chunkIndex] =
                        totalSoldiers;

                    ArchetypeChunk chunk =
                        chunks[chunkIndex];

                    var agentAccessor =
                        chunk.GetBufferAccessor(ref agentHandle);

                    var varianceAccessor =
                        chunk.GetBufferAccessor(ref varianceHandle);

                    for (int entityIndex = 0;
                         entityIndex < chunk.Count;
                         entityIndex++)
                    {
                        totalSoldiers +=
                            math.min(
                                agentAccessor[entityIndex].Length,
                                varianceAccessor[entityIndex].Length);
                    }
                }

                if (totalSoldiers <= 0)
                {
                    return 0;
                }

                if (totalSoldiers > _activeInstanceCapacity)
                {
                    RebuildRenderer(totalSoldiers);

                    if (!_initialized ||
                        totalSoldiers > _activeInstanceCapacity)
                    {
                        return 0;
                    }
                }

                int skinningClipCount =
                    _registeredGpuSkinningActive &&
                    gpuSkinningClipSet != null &&
                    gpuSkinningClipSet.Clips != null
                        ? gpuSkinningClipSet.Clips.Length
                        : 0;

                var job =
                    new CollectBrgInstanceDataJob
                    {
                        AgentHandle = agentHandle,
                        VarianceHandle = varianceHandle,
                        CombatHandle = combatHandle,
                        LocomotionHandle = locomotionHandle,
                        AnimationStateHandle = animationStateHandle,
                        AnimationReferenceHandle = animationReferenceHandle,
                        AnimationTuningHandle = animationTuningHandle,
                        DetectionHandle = detectionHandle,
                        LocalTransformHandle = localTransformHandle,

                        ChunkOffsets = chunkOffsets,
                        ParticleScale = particleScale,
                        SkinningClipCount = skinningClipCount,

                        OutPositionYaw = _positionYawArray,
                        OutOffsetScale = _offsetScaleArray,
                        OutAnimation = _animationArray
                    };

                JobHandle handle =
                    job.ScheduleParallel(
                        _ecsQuery,
                        default);

                handle.Complete();

                return totalSoldiers;
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

        private bool EnsureEcsQuery()
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
            _entityManager = world.EntityManager;

            _ecsQuery =
                _entityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<UnitTag>(),
                    ComponentType.ReadOnly<LocalTransform>(),
                    ComponentType.ReadOnly<UnitLocomotion>(),
                    ComponentType.ReadOnly<UnitAnimationState>(),
                    ComponentType.ReadOnly<UnitAnimationReference>(),
                    ComponentType.ReadOnly<SwarmAnimationApproximationTuning>(),
                    ComponentType.ReadOnly<DetectionTag>(),
                    ComponentType.ReadOnly<SoldierAgent>(),
                    ComponentType.ReadOnly<SoldierVariance>(),
                    ComponentType.ReadOnly<SoldierCombatState>());

            _queryReady = true;
            return true;
        }

        [BurstCompile]
        private struct CollectBrgInstanceDataJob : IJobChunk
        {
            [ReadOnly]
            public BufferTypeHandle<SoldierAgent> AgentHandle;

            [ReadOnly]
            public BufferTypeHandle<SoldierVariance> VarianceHandle;

            [ReadOnly]
            public BufferTypeHandle<SoldierCombatState> CombatHandle;

            [ReadOnly]
            public ComponentTypeHandle<UnitLocomotion> LocomotionHandle;

            [ReadOnly]
            public ComponentTypeHandle<UnitAnimationState> AnimationStateHandle;

            [ReadOnly]
            public ComponentTypeHandle<UnitAnimationReference> AnimationReferenceHandle;

            [ReadOnly]
            public ComponentTypeHandle<SwarmAnimationApproximationTuning> AnimationTuningHandle;

            [ReadOnly]
            public ComponentTypeHandle<DetectionTag> DetectionHandle;

            [ReadOnly]
            public ComponentTypeHandle<LocalTransform> LocalTransformHandle;

            [ReadOnly]
            public NativeArray<int> ChunkOffsets;

            [ReadOnly]
            public float ParticleScale;

            [ReadOnly]
            public int SkinningClipCount;

            [NativeDisableParallelForRestriction]
            public NativeArray<float4> OutPositionYaw;

            [NativeDisableParallelForRestriction]
            public NativeArray<float4> OutOffsetScale;

            [NativeDisableParallelForRestriction]
            public NativeArray<float4> OutAnimation;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                var agentAccessor =
                    chunk.GetBufferAccessor(ref AgentHandle);

                var varianceAccessor =
                    chunk.GetBufferAccessor(ref VarianceHandle);

                var combatAccessor =
                    chunk.GetBufferAccessor(ref CombatHandle);

                var locomotionArray =
                    chunk.GetNativeArray(ref LocomotionHandle);

                var animationStateArray =
                    chunk.GetNativeArray(ref AnimationStateHandle);

                var animationReferenceArray =
                    chunk.GetNativeArray(ref AnimationReferenceHandle);

                var animationTuningArray =
                    chunk.GetNativeArray(ref AnimationTuningHandle);

                var detectionArray =
                    chunk.GetNativeArray(ref DetectionHandle);

                var localTransformArray =
                    chunk.GetNativeArray(ref LocalTransformHandle);

                int outIndex =
                    ChunkOffsets[unfilteredChunkIndex];

                for (int entityIndex = 0;
                     entityIndex < chunk.Count;
                     entityIndex++)
                {
                    DynamicBuffer<SoldierAgent> agents =
                        agentAccessor[entityIndex];

                    DynamicBuffer<SoldierVariance> variances =
                        varianceAccessor[entityIndex];

                    DynamicBuffer<SoldierCombatState> combatStates =
                        combatAccessor[entityIndex];

                    UnitLocomotion locomotion =
                        locomotionArray[entityIndex];

                    UnitAnimationState animationState =
                        animationStateArray[entityIndex];

                    UnitAnimationReference animationReference =
                        animationReferenceArray[entityIndex];

                    SwarmAnimationApproximationTuning animationTuning =
                        animationTuningArray[entityIndex];

                    DetectionTag detection =
                        detectionArray[entityIndex];

                    LocalTransform localTransform =
                        localTransformArray[entityIndex];

                    int soldierCount =
                        math.min(
                            agents.Length,
                            variances.Length);

                    for (int soldierIndex = 0;
                         soldierIndex < soldierCount;
                         soldierIndex++)
                    {
                        SoldierAgent agent =
                            agents[soldierIndex];

                        SoldierVariance variance =
                            variances[soldierIndex];

                        SoldierCombatState combat = default;

                        if (soldierIndex < combatStates.Length)
                        {
                            combat = combatStates[soldierIndex];
                        }

                        float agentSpeed =
                            math.length(agent.Velocity);

                        LocomotionState visualState =
                            GetVisualLocomotionState(
                                locomotion.State,
                                agentSpeed,
                                in animationTuning);

                        float visualMotionSpeed =
                            GetVisualMotionSpeed(
                                animationReference.MotionSpeed,
                                agentSpeed,
                                visualState,
                                in animationTuning);

                        UnitAnimationClipId selectedClip =
                            ResolveSoldierClip(
                                animationState.Clip,
                                in combat);

                        float normalizedTime =
                            GetPayloadNormalizedTime(
                                in animationState,
                                selectedClip,
                                in variance,
                                in combat);

                        float playbackSpeed =
                            GetPayloadPlaybackSpeed(
                                in animationState,
                                selectedClip,
                                visualState,
                                visualMotionSpeed,
                                in combat);

                        float stateRenderScale =
                            GetRenderScale(
                                visualState,
                                in animationTuning);

                        float finalRenderScale =
                            math.max(
                                0.0001f,
                                ParticleScale * stateRenderScale);

                        OutPositionYaw[outIndex] =
                            new float4(
                                localTransform.Position,
                                agent.FacingYaw);

                        OutOffsetScale[outIndex] =
                            new float4(
                                agent.CurrentLocalPos,
                                finalRenderScale);

                        OutAnimation[outIndex] =
                            new float4(
                                GetSafeClipIndex(
                                    selectedClip,
                                    SkinningClipCount),
                                normalizedTime,
                                playbackSpeed,
                                detection.FactionId);

                        outIndex++;
                    }
                }
            }

            private static UnitAnimationClipId ResolveSoldierClip(
                UnitAnimationClipId unitClip,
                in SoldierCombatState combat)
            {
                return combat.InAttackRange
                    ? UnitAnimationClipId.Fight
                    : unitClip;
            }

            private static float GetPayloadNormalizedTime(
                in UnitAnimationState animationState,
                UnitAnimationClipId selectedClip,
                in SoldierVariance variance,
                in SoldierCombatState combat)
            {
                if (selectedClip == UnitAnimationClipId.Fight &&
                    combat.InAttackRange)
                {
                    float delay =
                        math.saturate(variance.AnimStartDelay);

                    float phaseOffset =
                        variance.Phase /
                        (2f * math.PI);

                    float t =
                        math.frac(
                            combat.AttackTimer +
                            phaseOffset);

                    return math.saturate(
                        (t - delay) /
                        math.max(
                            0.0001f,
                            1f - delay));
                }

                if (animationState.Mode ==
                    UnitAnimationMode.OneShot)
                {
                    float delay =
                        math.saturate(variance.AnimStartDelay);

                    return math.saturate(
                        (animationState.NormalizedTime - delay) /
                        math.max(
                            0.0001f,
                            1f - delay));
                }

                return math.frac(
                    animationState.NormalizedTime +
                    variance.Phase /
                    (2f * math.PI));
            }

            private static float GetPayloadPlaybackSpeed(
                in UnitAnimationState animationState,
                UnitAnimationClipId selectedClip,
                LocomotionState visualState,
                float visualMotionSpeed,
                in SoldierCombatState combat)
            {
                if (selectedClip == UnitAnimationClipId.Fight &&
                    combat.InAttackRange)
                {
                    return 1f;
                }

                float fallbackPlaybackRate =
                    GetPlaybackRate(
                        visualState,
                        visualMotionSpeed);

                return animationState.PlaybackSpeed > 0f
                    ? animationState.PlaybackSpeed
                    : fallbackPlaybackRate;
            }

            private static int GetSafeClipIndex(
                UnitAnimationClipId clip,
                int skinningClipCount)
            {
                int clipIndex =
                    GetClipIndex(clip);

                if (skinningClipCount <= 0)
                {
                    return math.max(0, clipIndex);
                }

                if (clipIndex >= 0 &&
                    clipIndex < skinningClipCount)
                {
                    return clipIndex;
                }

                if (clip == UnitAnimationClipId.Fight)
                {
                    return 0;
                }

                return math.clamp(
                    clipIndex,
                    0,
                    skinningClipCount - 1);
            }

            private static int GetClipIndex(
                UnitAnimationClipId clip)
            {
                if (clip == UnitAnimationClipId.Walk)
                {
                    return 1;
                }

                if (clip == UnitAnimationClipId.Run)
                {
                    return 2;
                }

                if (clip == UnitAnimationClipId.Fight)
                {
                    return 3;
                }

                return 0;
            }

            private static LocomotionState GetVisualLocomotionState(
                LocomotionState unitState,
                float agentSpeed,
                in SwarmAnimationApproximationTuning animationTuning)
            {
                if (unitState != LocomotionState.Idle)
                {
                    return unitState;
                }

                float walkThreshold =
                    math.max(
                        0.001f,
                        animationTuning.VisualWalkSpeedThreshold);

                float runThreshold =
                    math.max(
                        walkThreshold,
                        animationTuning.VisualRunSpeedThreshold);

                if (agentSpeed > runThreshold)
                {
                    return LocomotionState.Run;
                }

                if (agentSpeed > walkThreshold)
                {
                    return LocomotionState.Walk;
                }

                return LocomotionState.Idle;
            }

            private static float GetVisualMotionSpeed(
                float unitMotionSpeed,
                float agentSpeed,
                LocomotionState visualState,
                in SwarmAnimationApproximationTuning animationTuning)
            {
                if (visualState == LocomotionState.Idle)
                {
                    return unitMotionSpeed;
                }

                float motionDivisor =
                    math.max(
                        0.001f,
                        animationTuning.VisualMotionSpeedDivisor);

                float localMotion =
                    math.saturate(
                        agentSpeed /
                        motionDivisor);

                return math.max(
                    unitMotionSpeed,
                    localMotion);
            }

            private static float GetPlaybackRate(
                LocomotionState locomotionState,
                float motionSpeed)
            {
                if (locomotionState == LocomotionState.Idle)
                {
                    return 0.2f;
                }

                if (locomotionState == LocomotionState.Walk)
                {
                    return math.lerp(
                        0.6f,
                        1.1f,
                        math.saturate(motionSpeed));
                }

                return math.lerp(
                    1.0f,
                    1.6f,
                    math.saturate(motionSpeed));
            }

            private static float GetRenderScale(
                LocomotionState locomotionState,
                in SwarmAnimationApproximationTuning animationTuning)
            {
                if (locomotionState == LocomotionState.Idle)
                {
                    return animationTuning.IdleRenderScale;
                }

                if (locomotionState == LocomotionState.Walk)
                {
                    return animationTuning.WalkRenderScale;
                }

                return animationTuning.RunRenderScale;
            }
        }

        // ==========================================================
        // Instance Buffer Upload
        // ==========================================================

        private void UploadInstanceData(
            int instanceCount)
        {
            if (_instanceDataBuffer == null ||
                instanceCount <= 0)
            {
                return;
            }

            int positionYawStart =
                SwarmBrgInstanceProperties.GetPositionYawStartIndex(_layout);

            int offsetScaleStart =
                SwarmBrgInstanceProperties.GetOffsetScaleStartIndex(_layout);

            int animationStart =
                SwarmBrgInstanceProperties.GetAnimationStartIndex(_layout);

            _instanceDataBuffer.SetData(
                _positionYawArray,
                0,
                positionYawStart,
                instanceCount);

            _instanceDataBuffer.SetData(
                _offsetScaleArray,
                0,
                offsetScaleStart,
                instanceCount);

            _instanceDataBuffer.SetData(
                _animationArray,
                0,
                animationStart,
                instanceCount);
        }

        // ==========================================================
        // Draw Command Construction
        // ==========================================================

        private int GetSplitCountPerDrawUnit(
            int instanceCount)
        {
            if (instanceCount <= 0)
            {
                return 0;
            }

            if (submissionMode ==
                SwarmSubmissionMode.Integrated)
            {
                return 1;
            }

            return math.clamp(
                splitDrawCommandCount,
                1,
                instanceCount);
        }

        private int GetActiveDrawCommandCount(
            int instanceCount)
        {
            int drawUnitCount =
                GetActiveDrawUnitCount();

            if (instanceCount <= 0 ||
                drawUnitCount <= 0)
            {
                return 0;
            }

            return drawUnitCount *
                   GetSplitCountPerDrawUnit(instanceCount);
        }

        private unsafe JobHandle OnPerformCulling(
            BatchRendererGroup rendererGroup,
            BatchCullingContext cullingContext,
            BatchCullingOutput cullingOutput,
            IntPtr userContext)
        {
            int instanceCount =
                _activeInstanceCount;

            int drawUnitCount =
                GetActiveDrawUnitCount();

            if (!_initialized ||
                !_hasBatch ||
                instanceCount <= 0 ||
                drawUnitCount <= 0 ||
                _registeredMesh == null ||
                _runtimeMaterials == null ||
                _materialIDs == null ||
                _drawUnitSubMeshIndices == null)
            {
                WriteEmptyDrawCommands(cullingOutput);
                return default;
            }

            int splitCountPerDrawUnit =
                GetSplitCountPerDrawUnit(instanceCount);

            int drawCommandCount =
                drawUnitCount *
                splitCountPerDrawUnit;

            if (drawCommandCount <= 0)
            {
                WriteEmptyDrawCommands(cullingOutput);
                return default;
            }

            int alignment =
                UnsafeUtility.AlignOf<long>();

            var drawCommands =
                (BatchCullingOutputDrawCommands*)
                cullingOutput.drawCommands.GetUnsafePtr();

            drawCommands->drawCommands =
                (BatchDrawCommand*)UnsafeUtility.Malloc(
                    UnsafeUtility.SizeOf<BatchDrawCommand>() *
                    drawCommandCount,
                    alignment,
                    Allocator.TempJob);

            drawCommands->drawRanges =
                (BatchDrawRange*)UnsafeUtility.Malloc(
                    UnsafeUtility.SizeOf<BatchDrawRange>(),
                    alignment,
                    Allocator.TempJob);

            drawCommands->visibleInstances =
                (int*)UnsafeUtility.Malloc(
                    instanceCount * sizeof(int),
                    alignment,
                    Allocator.TempJob);

            drawCommands->indirectDrawCommands = null;
            drawCommands->proceduralDrawCommands = null;
            drawCommands->proceduralIndirectDrawCommands = null;
            drawCommands->drawCommandPickingEntityIds = null;
            drawCommands->instanceSortingPositions = null;
            drawCommands->instanceSortingPositionFloatCount = 0;

            drawCommands->drawCommandCount =
                drawCommandCount;

            drawCommands->indirectDrawCommandCount = 0;
            drawCommands->proceduralDrawCommandCount = 0;
            drawCommands->proceduralIndirectDrawCommandCount = 0;
            drawCommands->drawRangeCount = 1;
            drawCommands->visibleInstanceCount = instanceCount;

            for (int i = 0; i < instanceCount; i++)
            {
                drawCommands->visibleInstances[i] = i;
            }

            int baseVisibleCount =
                instanceCount /
                splitCountPerDrawUnit;

            int remainder =
                instanceCount %
                splitCountPerDrawUnit;

            int commandIndex = 0;

            for (int drawUnitIndex = 0;
                 drawUnitIndex < drawUnitCount;
                 drawUnitIndex++)
            {
                int subMeshIndex =
                    _drawUnitSubMeshIndices[drawUnitIndex];

                BatchMaterialID materialID =
                    _materialIDs[drawUnitIndex];

                int visibleOffset = 0;

                for (int splitIndex = 0;
                     splitIndex < splitCountPerDrawUnit;
                     splitIndex++)
                {
                    int visibleCount =
                        baseVisibleCount +
                        (splitIndex < remainder ? 1 : 0);

                    drawCommands->drawCommands[commandIndex] =
                        new BatchDrawCommand
                        {
                            flags = BatchDrawCommandFlags.None,
                            batchID = _batchID,
                            materialID = materialID,
                            meshID = _meshID,
                            visibleOffset = (uint)visibleOffset,
                            visibleCount = (uint)visibleCount,
                            submeshIndex = (ushort)subMeshIndex,
                            splitVisibilityMask = 0xff,
                            sortingPosition = 0,
                            lightmapIndex = 0,
                            activeMeshLod =
                                ForcedMeshLod
                        };

                    visibleOffset += visibleCount;
                    commandIndex++;
                }
            }

            drawCommands->drawRanges[0] =
                new BatchDrawRange
                {
                    drawCommandsType =
                        BatchDrawCommandType.Direct,

                    drawCommandsBegin = 0,
                    drawCommandsCount =
                        (uint)drawCommandCount,

                    filterSettings =
                        new BatchFilterSettings
                        {
                            renderingLayerMask =
                                AllRenderingLayers,
                            layer = (byte)gameObject.layer,
                            motionMode =
                                MotionVectorGenerationMode.ForceNoMotion,
                            shadowCastingMode =
                                ShadowCastingMode.Off,
                            receiveShadows = false,
                            staticShadowCaster = false,
                            allDepthSorted = false
                        }
                };

            return default;
        }

        private unsafe void WriteEmptyDrawCommands(
            BatchCullingOutput cullingOutput)
        {
            var drawCommands =
                (BatchCullingOutputDrawCommands*)
                cullingOutput.drawCommands.GetUnsafePtr();

            drawCommands->drawCommands = null;
            drawCommands->indirectDrawCommands = null;
            drawCommands->proceduralDrawCommands = null;
            drawCommands->proceduralIndirectDrawCommands = null;
            drawCommands->drawRanges = null;
            drawCommands->visibleInstances = null;
            drawCommands->instanceSortingPositions = null;
            drawCommands->drawCommandPickingEntityIds = null;

            drawCommands->drawCommandCount = 0;
            drawCommands->indirectDrawCommandCount = 0;
            drawCommands->proceduralDrawCommandCount = 0;
            drawCommands->proceduralIndirectDrawCommandCount = 0;
            drawCommands->drawRangeCount = 0;
            drawCommands->visibleInstanceCount = 0;
            drawCommands->instanceSortingPositionFloatCount = 0;
        }

        // ==========================================================
        // Stats
        // ==========================================================

        private void UpdateStatsForEmptyFrame()
        {
            if (!enableInstrumentation)
            {
                return;
            }

            _stats.ApplySubmissionMode(submissionMode);

            _stats.SetInstanceInfo(
                0,
                _activeInstanceCapacity,
                _layout.TotalSizeBytes);

            _stats.SetResourceInfo(
                _registeredMesh != null ? 1 : 0,
                GetActiveDrawUnitCount());

            _stats.SetSubmissionInfo(
                batchCount: _hasBatch ? 1 : 0,
                drawRangeCount: 0,
                drawCommandCount: 0);

            _stats.MarkInvalid(Time.frameCount);
        }

        // ==========================================================
        // Release
        // ==========================================================

        private void ReleaseRenderer()
        {
            _activeInstanceCount = 0;
            _hasBatch = false;
            _initialized = false;

            if (_brg != null)
            {
                _brg.Dispose();
                _brg = null;
            }

            if (_instanceDataBuffer != null)
            {
                _instanceDataBuffer.Release();
                _instanceDataBuffer = null;
            }

            DisposePersistentInstanceArrays();
            ReleaseSkinningBuffers();
            ReleaseRuntimeMaterials();

            _zeroArray = null;
            _registeredMesh = null;
            _registeredGpuSkinningActive = false;

            _activeInstanceCapacity = 0;
            _materialBindingDirty = true;
            _appliedYOffset = float.NaN;
        }
    }
}
