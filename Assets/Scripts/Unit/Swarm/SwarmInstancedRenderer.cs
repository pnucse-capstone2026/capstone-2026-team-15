using Unity.Burst;
using Unity.Burst.Intrinsics;
using Detection;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

namespace Swarm
{
    // 완전한 3D 인스턴스 렌더링을 위한 GPU 입력 데이터.
    // float3 2개 + float 5개 = 44 bytes.
    public struct GPUParticleData
    {
        public float3 UnitPos;      // 12 bytes
        public float3 LocalOffset;  // 12 bytes
        public float FacingYaw;     // 4 bytes
        public float Phase;         // 4 bytes
        public float PhaseSpeed;    // 4 bytes
        public float MotionScale;   // 4 bytes
        public float RenderScale;   // 4 bytes
    }

    // 병사 인스턴스별 애니메이션 payload.
    // GPU 스키닝 셰이더에서 클립, 시간, 재생 속도를 참조.
    public struct SoldierAnimationPayload
    {
        public int ClipIndex;
        public float NormalizedTime;
        public float PlaybackSpeed;
        public float Padding;
    }

    // GPU 스키닝 클립 정보.
    public struct SkinningClipInfo
    {
        public int StartFrame;
        public int FrameCount;
        public float Length;
        public float Padding;
    }

    public sealed class SwarmInstancedRenderer : MonoBehaviour
    {
        [Header("Assets")]
        [SerializeField] private ComputeShader swarmCompute;
        [SerializeField] private Material material;
        [SerializeField] private Material[] materials;
        [SerializeField] private Mesh mesh;
        [SerializeField] private GpuSkinningClipSet gpuSkinningClipSet;

        [Header("Settings")]
        [SerializeField] private float particleScale = 1.0f;
        [SerializeField] private float yOffset = 0.0f;
        [SerializeField] private bool enableGpuSkinning = false;

        private GraphicsBuffer _matrixBuffer;
        private GraphicsBuffer[] _commandBuffers;
        private GraphicsBuffer _particleInputBuffer;
        private GraphicsBuffer _animationPayloadBuffer;
        private GraphicsBuffer _instanceColorBuffer;
        private GraphicsBuffer _skinningMatrixBuffer;
        private GraphicsBuffer _skinningClipInfoBuffer;
        private GpuSkinningClipSet _uploadedSkinningClipSet;

        private readonly uint[] _indirectArgs = new uint[5];

        private EntityQuery _query;
        private World _world;
        private EntityManager _entityManager;
        private bool _queryReady;
        private Camera _mainCamera;

        private static readonly int MatricesPropertyID = Shader.PropertyToID("_VisibleMatrices");
        private static readonly int AnimationPayloadPropertyID = Shader.PropertyToID("_SoldierAnimationPayloads");
        private static readonly int InstanceColorsPropertyID = Shader.PropertyToID("_InstanceColors");
        private static readonly int SkinningMatricesPropertyID = Shader.PropertyToID("_SkinningMatrices");
        private static readonly int SkinningClipInfosPropertyID = Shader.PropertyToID("_SkinningClipInfos");
        private static readonly int SkinningEnabledPropertyID = Shader.PropertyToID("_SkinningEnabled");
        private static readonly int SkinningBoneCountPropertyID = Shader.PropertyToID("_SkinningBoneCount");

        private MaterialPropertyBlock _matProps;

        private void Awake()
        {
            _mainCamera = Camera.main;
            _matProps = new MaterialPropertyBlock();

            EnsureQuery();
        }

        private bool EnsureQuery()
        {
            World world = World.DefaultGameObjectInjectionWorld;

            if (world == null || !world.IsCreated)
            {
                _queryReady = false;
                return false;
            }

            if (_queryReady && _world == world)
            {
                return true;
            }

            _world = world;
            _entityManager = world.EntityManager;

            _query = _entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<UnitTag>(),
                ComponentType.ReadOnly<DetectionTag>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<UnitLocomotion>(),
                ComponentType.ReadOnly<UnitAnimationState>(),
                ComponentType.ReadOnly<UnitAnimationReference>(),
                ComponentType.ReadOnly<UnitAnimationProfile>(),
                ComponentType.ReadOnly<SwarmAnimationApproximationTuning>(),
                ComponentType.ReadOnly<SoldierAgent>(),
                ComponentType.ReadOnly<SoldierVariance>(),
                ComponentType.ReadOnly<SoldierCombatState>()
            );

            _queryReady = true;
            return true;
        }

        private void OnDestroy()
        {
            ReleaseBuffers();
        }

        private void ReleaseBuffers()
        {
            _matrixBuffer?.Release();
            _matrixBuffer = null;

            _particleInputBuffer?.Release();
            _particleInputBuffer = null;

            _animationPayloadBuffer?.Release();
            _animationPayloadBuffer = null;

            _instanceColorBuffer?.Release();
            _instanceColorBuffer = null;

            _skinningMatrixBuffer?.Release();
            _skinningMatrixBuffer = null;

            _skinningClipInfoBuffer?.Release();
            _skinningClipInfoBuffer = null;

            if (_commandBuffers == null)
            {
                return;
            }

            for (int i = 0; i < _commandBuffers.Length; i++)
            {
                _commandBuffers[i]?.Release();
            }

            _commandBuffers = null;
        }

        private void Update()
        {
            Mesh renderMesh = GetRenderMesh();

            if (swarmCompute == null || renderMesh == null)
            {
                return;
            }

            Material[] activeMaterials = GetActiveMaterials();

            if (activeMaterials.Length == 0)
            {
                return;
            }

            _mainCamera ??= Camera.main;

            if (_mainCamera == null)
            {
                return;
            }

            if (!EnsureQuery())
            {
                return;
            }

            if (_query.IsEmpty)
            {
                return;
            }

            _query.CompleteDependency();

            var em = _entityManager;

            using var chunks = _query.ToArchetypeChunkArray(Allocator.TempJob);

            var agentHandle = em.GetBufferTypeHandle<SoldierAgent>(true);
            var varHandle = em.GetBufferTypeHandle<SoldierVariance>(true);
            var combatHandle = em.GetBufferTypeHandle<SoldierCombatState>(true);

            var locomotionHandle = em.GetComponentTypeHandle<UnitLocomotion>(true);
            var animationStateHandle = em.GetComponentTypeHandle<UnitAnimationState>(true);
            var animRefHandle = em.GetComponentTypeHandle<UnitAnimationReference>(true);
            var animationProfileHandle = em.GetComponentTypeHandle<UnitAnimationProfile>(true);
            var animationTuningHandle = em.GetComponentTypeHandle<SwarmAnimationApproximationTuning>(true);
            var rangedStateHandle = em.GetComponentTypeHandle<RangedAttackState>(true);
            var detectionTagHandle = em.GetComponentTypeHandle<DetectionTag>(true);
            var ltHandle = em.GetComponentTypeHandle<LocalTransform>(true);

            NativeArray<int> chunkOffsets = default;
            NativeArray<GPUParticleData> allParticles = default;
            NativeArray<SoldierAnimationPayload> allAnimationPayloads = default;
            NativeArray<float4> allInstanceColors = default;

            try
            {
                int totalSoldiers = 0;

                chunkOffsets = new NativeArray<int>(
                    chunks.Length,
                    Allocator.TempJob);

                for (int i = 0; i < chunks.Length; i++)
                {
                    chunkOffsets[i] = totalSoldiers;

                    var buffers = chunks[i].GetBufferAccessor(ref agentHandle);

                    for (int j = 0; j < buffers.Length; j++)
                    {
                        totalSoldiers += buffers[j].Length;
                    }
                }

                if (totalSoldiers <= 0)
                {
                    return;
                }

                ValidateBuffers(totalSoldiers);

                bool skinningActive = UploadSkinningDataIfNeeded();

                allParticles = new NativeArray<GPUParticleData>(
                    totalSoldiers,
                    Allocator.TempJob);

                allAnimationPayloads = new NativeArray<SoldierAnimationPayload>(
                    totalSoldiers,
                    Allocator.TempJob);

                allInstanceColors = new NativeArray<float4>(
                    totalSoldiers,
                    Allocator.TempJob);

                var job = new CollectDataJob
                {
                    AgentHandle = agentHandle,
                    VarHandle = varHandle,
                    CombatHandle = combatHandle,

                    LocomotionHandle = locomotionHandle,
                    AnimationStateHandle = animationStateHandle,
                    AnimationRefHandle = animRefHandle,
                    AnimationProfileHandle = animationProfileHandle,
                    AnimationTuningHandle = animationTuningHandle,
                    RangedStateHandle = rangedStateHandle,
                    DetectionTagHandle = detectionTagHandle,
                    LtHandle = ltHandle,

                    ChunkOffsets = chunkOffsets,
                    GlobalTime = Time.time,

                    SkinningClipCount =
                        skinningActive &&
                        gpuSkinningClipSet != null &&
                        gpuSkinningClipSet.Clips != null
                            ? gpuSkinningClipSet.Clips.Length
                            : 0,

                    OutParticles = allParticles,
                    OutAnimationPayloads = allAnimationPayloads,
                    OutInstanceColors = allInstanceColors
                };

                JobHandle handle = job.ScheduleParallel(_query, default);
                handle.Complete();

                _particleInputBuffer.SetData(allParticles);
                _animationPayloadBuffer.SetData(allAnimationPayloads);
                _instanceColorBuffer.SetData(allInstanceColors);

                int kernel = swarmCompute.FindKernel("CSMain");

                swarmCompute.SetBuffer(kernel, "_Particles", _particleInputBuffer);
                swarmCompute.SetBuffer(kernel, "_VisibleMatrices", _matrixBuffer);
                swarmCompute.SetInt("_TotalParticleCount", totalSoldiers);
                swarmCompute.SetFloat("_ParticleScale", particleScale);
                swarmCompute.SetFloat("_YOffset", yOffset);

                int dispatchCount = Mathf.CeilToInt(totalSoldiers / 64f);
                swarmCompute.Dispatch(kernel, dispatchCount, 1, 1);

                _matProps.SetBuffer(MatricesPropertyID, _matrixBuffer);
                _matProps.SetBuffer(InstanceColorsPropertyID, _instanceColorBuffer);
                _matProps.SetInt(SkinningEnabledPropertyID, skinningActive ? 1 : 0);

                if (skinningActive)
                {
                    _matProps.SetBuffer(AnimationPayloadPropertyID, _animationPayloadBuffer);
                    _matProps.SetBuffer(SkinningMatricesPropertyID, _skinningMatrixBuffer);
                    _matProps.SetBuffer(SkinningClipInfosPropertyID, _skinningClipInfoBuffer);
                    _matProps.SetInt(SkinningBoneCountPropertyID, gpuSkinningClipSet.BoneCount);
                }

                int drawCount = math.min(renderMesh.subMeshCount, activeMaterials.Length);

                EnsureCommandBuffers(drawCount);

                for (int subMesh = 0; subMesh < drawCount; subMesh++)
                {
                    Material drawMaterial = activeMaterials[subMesh];

                    if (drawMaterial == null)
                    {
                        continue;
                    }

                    _indirectArgs[0] = renderMesh.GetIndexCount(subMesh);
                    _indirectArgs[1] = (uint)totalSoldiers;
                    _indirectArgs[2] = renderMesh.GetIndexStart(subMesh);
                    _indirectArgs[3] = (uint)renderMesh.GetBaseVertex(subMesh);
                    _indirectArgs[4] = 0;

                    GraphicsBuffer commandBuffer = _commandBuffers[subMesh];
                    commandBuffer.SetData(_indirectArgs);

                    RenderParams rp = new RenderParams(drawMaterial)
                    {
                        worldBounds = new Bounds(Vector3.zero, Vector3.one * 10000f),
                        shadowCastingMode = ShadowCastingMode.On,
                        matProps = _matProps
                    };

                    Graphics.RenderMeshIndirect(
                        rp,
                        renderMesh,
                        commandBuffer);
                }
            }
            finally
            {
                if (chunkOffsets.IsCreated)
                {
                    chunkOffsets.Dispose();
                }

                if (allParticles.IsCreated)
                {
                    allParticles.Dispose();
                }

                if (allAnimationPayloads.IsCreated)
                {
                    allAnimationPayloads.Dispose();
                }

                if (allInstanceColors.IsCreated)
                {
                    allInstanceColors.Dispose();
                }
            }
        }

        private void ValidateBuffers(int count)
        {
            int capacity = Mathf.NextPowerOfTwo(count);

            if (_matrixBuffer == null || _matrixBuffer.count < count)
            {
                _matrixBuffer?.Release();
                _matrixBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    capacity,
                    64);
            }

            if (_particleInputBuffer == null || _particleInputBuffer.count < count)
            {
                _particleInputBuffer?.Release();
                _particleInputBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    capacity,
                    44);
            }

            if (_animationPayloadBuffer == null || _animationPayloadBuffer.count < count)
            {
                _animationPayloadBuffer?.Release();
                _animationPayloadBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    capacity,
                    16);
            }

            if (_instanceColorBuffer == null || _instanceColorBuffer.count < count)
            {
                _instanceColorBuffer?.Release();
                _instanceColorBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    capacity,
                    16);
            }
        }

        private Mesh GetRenderMesh()
        {
            if (
                enableGpuSkinning &&
                gpuSkinningClipSet != null &&
                gpuSkinningClipSet.IsValid &&
                gpuSkinningClipSet.SourceMesh != null)
            {
                return gpuSkinningClipSet.SourceMesh;
            }

            return mesh;
        }

        private bool UploadSkinningDataIfNeeded()
        {
            if (
                !enableGpuSkinning ||
                gpuSkinningClipSet == null ||
                !gpuSkinningClipSet.IsValid)
            {
                return false;
            }

            if (
                _uploadedSkinningClipSet == gpuSkinningClipSet &&
                _skinningMatrixBuffer != null &&
                _skinningClipInfoBuffer != null)
            {
                return true;
            }

            _skinningMatrixBuffer?.Release();
            _skinningClipInfoBuffer?.Release();

            Matrix4x4[] matrices = gpuSkinningClipSet.BoneMatrices;

            _skinningMatrixBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                matrices.Length,
                64);

            _skinningMatrixBuffer.SetData(matrices);

            var clips = gpuSkinningClipSet.Clips;
            var clipInfos = new SkinningClipInfo[clips.Length];

            for (int i = 0; i < clips.Length; i++)
            {
                clipInfos[i] = new SkinningClipInfo
                {
                    StartFrame = clips[i].StartFrame,
                    FrameCount = clips[i].FrameCount,
                    Length = clips[i].Length,
                    Padding = 0f
                };
            }

            _skinningClipInfoBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                clipInfos.Length,
                16);

            _skinningClipInfoBuffer.SetData(clipInfos);

            _uploadedSkinningClipSet = gpuSkinningClipSet;

            return true;
        }

        private void EnsureCommandBuffers(int drawCount)
        {
            if (_commandBuffers != null && _commandBuffers.Length == drawCount)
            {
                bool allValid = true;

                for (int i = 0; i < _commandBuffers.Length; i++)
                {
                    if (_commandBuffers[i] != null)
                    {
                        continue;
                    }

                    allValid = false;
                    break;
                }

                if (allValid)
                {
                    return;
                }
            }

            if (_commandBuffers != null)
            {
                for (int i = 0; i < _commandBuffers.Length; i++)
                {
                    _commandBuffers[i]?.Release();
                }
            }

            _commandBuffers = new GraphicsBuffer[drawCount];

            for (int i = 0; i < drawCount; i++)
            {
                _commandBuffers[i] = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments |
                    GraphicsBuffer.Target.Structured,
                    1,
                    sizeof(uint) * 5);
            }
        }

        private Material[] GetActiveMaterials()
        {
            if (materials != null && materials.Length > 0)
            {
                return materials;
            }

            if (material == null)
            {
                return System.Array.Empty<Material>();
            }

            return new[] { material };
        }

        [BurstCompile]
        private struct CollectDataJob : IJobChunk
        {
            private const float BowShootDuration = 2.0f;

            [ReadOnly] public BufferTypeHandle<SoldierAgent> AgentHandle;
            [ReadOnly] public BufferTypeHandle<SoldierVariance> VarHandle;
            [ReadOnly] public BufferTypeHandle<SoldierCombatState> CombatHandle;

            [ReadOnly] public ComponentTypeHandle<UnitLocomotion> LocomotionHandle;
            [ReadOnly] public ComponentTypeHandle<UnitAnimationState> AnimationStateHandle;
            [ReadOnly] public ComponentTypeHandle<UnitAnimationReference> AnimationRefHandle;
            [ReadOnly] public ComponentTypeHandle<UnitAnimationProfile> AnimationProfileHandle;
            [ReadOnly] public ComponentTypeHandle<SwarmAnimationApproximationTuning> AnimationTuningHandle;
            [ReadOnly] public ComponentTypeHandle<RangedAttackState> RangedStateHandle;
            [ReadOnly] public ComponentTypeHandle<DetectionTag> DetectionTagHandle;
            [ReadOnly] public ComponentTypeHandle<LocalTransform> LtHandle;

            [ReadOnly] public NativeArray<int> ChunkOffsets;
            [ReadOnly] public float GlobalTime;
            [ReadOnly] public int SkinningClipCount;

            [NativeDisableParallelForRestriction]
            public NativeArray<GPUParticleData> OutParticles;

            [NativeDisableParallelForRestriction]
            public NativeArray<SoldierAnimationPayload> OutAnimationPayloads;

            [NativeDisableParallelForRestriction]
            public NativeArray<float4> OutInstanceColors;

            public void Execute(
                in ArchetypeChunk chunk,
                int unfilteredChunkIndex,
                bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                var agentAccessor = chunk.GetBufferAccessor(ref AgentHandle);
                var varAccessor = chunk.GetBufferAccessor(ref VarHandle);
                var combatAccessor = chunk.GetBufferAccessor(ref CombatHandle);

                var locomotionArray = chunk.GetNativeArray(ref LocomotionHandle);
                var animationStates = chunk.GetNativeArray(ref AnimationStateHandle);
                var animationRefs = chunk.GetNativeArray(ref AnimationRefHandle);
                var animationProfiles = chunk.GetNativeArray(ref AnimationProfileHandle);
                var animationTunings = chunk.GetNativeArray(ref AnimationTuningHandle);
                var detectionTags = chunk.GetNativeArray(ref DetectionTagHandle);
                bool hasRangedState = chunk.Has(ref RangedStateHandle);
                var rangedStates = hasRangedState
                    ? chunk.GetNativeArray(ref RangedStateHandle)
                    : default;
                var ltArray = chunk.GetNativeArray(ref LtHandle);

                int outIdx = ChunkOffsets[unfilteredChunkIndex];

                for (int i = 0; i < chunk.Count; i++)
                {
                    var agentBuffer = agentAccessor[i];
                    var varBuffer = varAccessor[i];
                    var combatBuffer = combatAccessor[i];

                    UnitLocomotion locomotion = locomotionArray[i];
                    UnitAnimationState animationState = animationStates[i];
                    UnitAnimationReference animationRef = animationRefs[i];
                    UnitAnimationProfile animationProfile = animationProfiles[i];
                    SwarmAnimationApproximationTuning animationTuning = animationTunings[i];
                    RangedAttackState rangedState = hasRangedState
                        ? rangedStates[i]
                        : default;
                    DetectionTag detectionTag = detectionTags[i];

                    float3 unitPos = ltArray[i].Position;

                    for (int j = 0; j < agentBuffer.Length; j++)
                    {
                        SoldierAgent agent = agentBuffer[j];
                        SoldierVariance variance = varBuffer[j];

                        SoldierCombatState combat = default;
                        bool hasCombatState = j < combatBuffer.Length;

                        if (hasCombatState)
                        {
                            combat = combatBuffer[j];
                        }

                        float agentSpeed = math.length(agent.Velocity);

                        LocomotionState visualState = GetVisualLocomotionState(
                            locomotion.State,
                            agentSpeed,
                            animationTuning);

                        float visualMotionSpeed = GetVisualMotionSpeed(
                            animationRef.MotionSpeed,
                            agentSpeed,
                            visualState,
                            animationTuning);

                        float visualPhaseSpeed = math.max(
                            agent.AnimPhaseSpeed,
                            GetFallbackPhaseSpeed(
                                visualState,
                                visualMotionSpeed));

                        float visualPhase = variance.Phase;

                        if (agent.AnimPhaseSpeed <= 0.01f &&
                            visualState != LocomotionState.Idle)
                        {
                            visualPhase += GlobalTime * visualPhaseSpeed;
                        }

                        UnitAnimationClipId selectedClip = ResolveSoldierClip(
                            animationState.Clip,
                            animationProfile.PrimaryAttackClip,
                            combat,
                            rangedState);
                        OutInstanceColors[outIdx] =
                            GetFactionColor(detectionTag.FactionId);

                        float payloadNormalizedTime = GetPayloadNormalizedTime(
                            animationState,
                            selectedClip,
                            variance,
                            combat,
                            rangedState);

                        float playbackSpeed = GetPayloadPlaybackSpeed(
                            animationState,
                            selectedClip,
                            visualState,
                            visualMotionSpeed,
                            combat,
                            rangedState);

                        OutParticles[outIdx] = new GPUParticleData
                        {
                            UnitPos = unitPos,
                            LocalOffset = agent.CurrentLocalPos,
                            FacingYaw = agent.FacingYaw,
                            Phase = visualPhase,
                            PhaseSpeed = visualPhaseSpeed,
                            MotionScale = math.clamp(visualMotionSpeed, 0f, 1f),
                            RenderScale = GetRenderScale(
                                visualState,
                                animationTuning)
                        };

                        OutAnimationPayloads[outIdx] = new SoldierAnimationPayload
                        {
                            ClipIndex = GetSafeClipIndex(
                                selectedClip,
                                SkinningClipCount),

                            NormalizedTime = payloadNormalizedTime,
                            PlaybackSpeed = playbackSpeed,
                            Padding = 0f
                        };

                        outIdx++;
                    }
                }
            }

            /// <summary>
            /// 병사별 전투 상태를 반영하여 최종 클립을 결정한다.
            /// 근접 공격은 병사별 InAttackRange, 원거리 공격은 유닛 사격 상태로 공격 클립을 override한다.
            /// </summary>
            private static UnitAnimationClipId ResolveSoldierClip(
                UnitAnimationClipId unitClip,
                UnitAnimationClipId primaryAttackClip,
                in SoldierCombatState combat,
                in RangedAttackState rangedState)
            {
                if (combat.InAttackRange &&
                    primaryAttackClip == UnitAnimationClipId.Fight)
                {
                    return UnitAnimationClipId.Fight;
                }

                if (rangedState.IsShooting &&
                    primaryAttackClip == UnitAnimationClipId.BowShoot)
                {
                    return UnitAnimationClipId.BowShoot;
                }

                return unitClip;
            }

            /// <summary>
            /// 선택된 클립에 맞는 NormalizedTime 계산.
            /// Fight override의 경우 SoldierCombatState.AttackTimer를 사용해 병사별 전투 위상을 만든다.
            /// </summary>
            private static float GetPayloadNormalizedTime(
                in UnitAnimationState animationState,
                UnitAnimationClipId selectedClip,
                in SoldierVariance variance,
                in SoldierCombatState combat,
                in RangedAttackState rangedState)
            {
                if (selectedClip == UnitAnimationClipId.Fight && combat.InAttackRange)
                {
                    float delay = math.saturate(variance.AnimStartDelay);

                    float phaseOffset = variance.Phase / (2f * math.PI);
                    float t = math.frac(combat.AttackTimer + phaseOffset);

                    return math.saturate(
                        (t - delay) /
                        math.max(0.0001f, 1f - delay));
                }

                if (selectedClip == UnitAnimationClipId.BowShoot && rangedState.IsShooting)
                {
                    float delay = math.saturate(variance.AnimStartDelay);
                    float t = math.saturate(
                        (BowShootDuration - rangedState.ShootAnimationTimer) /
                        BowShootDuration);

                    return math.saturate(
                        (t - delay) /
                        math.max(0.0001f, 1f - delay));
                }

                if (animationState.Mode == UnitAnimationMode.OneShot)
                {
                    float delay = math.saturate(variance.AnimStartDelay);

                    return math.saturate(
                        (animationState.NormalizedTime - delay) /
                        math.max(0.0001f, 1f - delay));
                }

                return math.frac(
                    animationState.NormalizedTime +
                    variance.Phase / (math.PI * 2f));
            }

            /// <summary>
            /// 선택된 클립에 맞는 재생 속도 계산.
            /// Fight override는 기본 1.0으로 재생한다.
            /// </summary>
            private static float GetPayloadPlaybackSpeed(
                in UnitAnimationState animationState,
                UnitAnimationClipId selectedClip,
                LocomotionState visualState,
                float visualMotionSpeed,
                in SoldierCombatState combat,
                in RangedAttackState rangedState)
            {
                if (selectedClip == UnitAnimationClipId.Fight && combat.InAttackRange)
                {
                    return 1f;
                }

                if (selectedClip == UnitAnimationClipId.BowShoot && rangedState.IsShooting)
                {
                    return 1f / BowShootDuration;
                }

                float fallbackPlaybackRate = GetPlaybackRate(
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
                int clipIndex = GetClipIndex(clip);

                if (clip == UnitAnimationClipId.BowShoot)
                {
                    if (clipIndex >= 0 && clipIndex < skinningClipCount)
                    {
                        return clipIndex;
                    }

                    int fightIndex =
                        GetClipIndex(UnitAnimationClipId.Fight);

                    if (fightIndex >= 0 && fightIndex < skinningClipCount)
                    {
                        return fightIndex;
                    }

                    return 0;
                }

                if (skinningClipCount <= 0)
                {
                    return math.max(0, clipIndex);
                }

                if (clipIndex >= 0 && clipIndex < skinningClipCount)
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

            private static int GetClipIndex(UnitAnimationClipId clip)
            {
                if (clip == UnitAnimationClipId.Idle)
                {
                    return 0;
                }

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

                if (clip == UnitAnimationClipId.BowShoot)
                {
                    return 4;
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

                float walkThreshold = math.max(
                    0.001f,
                    animationTuning.VisualWalkSpeedThreshold);

                float runThreshold = math.max(
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

                float motionDivisor = math.max(
                    0.001f,
                    animationTuning.VisualMotionSpeedDivisor);

                float localMotion = math.saturate(agentSpeed / motionDivisor);

                return math.max(unitMotionSpeed, localMotion);
            }

            private static float GetFallbackPhaseSpeed(
                LocomotionState visualState,
                float motionSpeed)
            {
                if (visualState == LocomotionState.Idle)
                {
                    return 0f;
                }

                if (visualState == LocomotionState.Walk)
                {
                    return math.max(2.1f, motionSpeed * 4.2f);
                }

                return math.max(4.75f, motionSpeed * 5.5f);
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

            private static float4 GetFactionColor(int factionId)
            {
                if (factionId < 0)
                {
                    return new float4(0.55f, 0.55f, 0.55f, 1.00f);
                }

                switch (factionId % 8)
                {
                    case 0:
                        return new float4(0.20f, 0.45f, 1.00f, 1.00f);
                    case 1:
                        return new float4(1.00f, 0.22f, 0.18f, 1.00f);
                    case 2:
                        return new float4(0.18f, 0.80f, 0.32f, 1.00f);
                    case 3:
                        return new float4(1.00f, 0.86f, 0.18f, 1.00f);
                    case 4:
                        return new float4(0.15f, 0.85f, 0.95f, 1.00f);
                    case 5:
                        return new float4(0.90f, 0.25f, 1.00f, 1.00f);
                    case 6:
                        return new float4(1.00f, 0.55f, 0.12f, 1.00f);
                    default:
                        return new float4(0.92f, 0.92f, 0.92f, 1.00f);
                }
            }
        }
    }
}
