using UnityEngine;
using UnityEngine.Rendering;

namespace Swarm
{
    /// <summary>
    /// 렌더러가 BenchmarkRunner에 제공하는 현재 프레임 구조 계측값.
    /// 프레임 시간 및 하드웨어 성능 지표는 BenchmarkRunner가 별도로 측정한다.
    /// </summary>
    public struct SwarmRendererInstrumentationSample
    {
        public bool IsValid;
        public int FrameIndex;

        // 공통 필수 계측 항목
        public SwarmRenderPipelineCase RenderCase;
        public SwarmSubmissionMode SubmissionMode;
        public int InstanceCount;
        public int ActiveSubMeshCount;
        public int ActiveMaterialCount;
        public int CommandsPerSubMesh;
        public int SubmittedCommandCount;
        public int ApiCallCount;
        public int UploadedBytesPerFrame;

        // RenderMeshIndirect 전용
        public int ComputeDispatchCount;

        // BatchRendererGroup 전용
        public int BatchCount;
        public int DrawRangeCount;
        public int BatchDrawCommandCount;
    }

    /// <summary>
    /// 두 렌더러에서 동일해야 하는 실험 통제 조건.
    ///
    /// BenchmarkRunner는 씬 로드 후 측정 시작 전에 이 값을 비교한다.
    /// 프레임별 계측 대상이 아니므로 측정 구간 중에는 다시 계산하지 않는다.
    /// </summary>
    public struct SwarmRendererControlState
    {
        // 컬링 및 공간 범위
        public SwarmCullingMode CullingMode;
        public Vector3 BoundsCenter;
        public Vector3 BoundsSize;

        // ECS 결과를 화면에 배치할 때 사용하는 공통 시각 파라미터
        public float ParticleScale;
        public float YOffset;

        // GPU 스키닝 및 실제 렌더 자원
        public bool GpuSkinningRequested;
        public bool GpuSkinningActive;
        public GpuSkinningClipSet GpuSkinningClipSet;
        public Mesh RenderMesh;
        public int ActiveMaterialCount;

        // 제출 구조
        public int SplitCommandCount;

        // 렌더 상태
        public int Layer;
        public uint RenderingLayerMask;
        public ShadowCastingMode ShadowCastingMode;
        public bool ReceiveShadows;
        public MotionVectorGenerationMode MotionVectorMode;
        public int ForcedMeshLod;
    }

    /// <summary>
    /// BenchmarkRunner가 두 렌더러를 공통 방식으로 제어하고
    /// 현재 프레임 계측값과 실험 통제 조건을 읽기 위한 인터페이스.
    ///
    /// 렌더러는 CSV 등 파일 저장을 수행하지 않는다.
    /// </summary>
    public interface ISwarmRendererInstrumentationSource
    {
        bool InstrumentationEnabled { get; }

        SwarmRenderPipelineCase InstrumentationCase
        {
            get;
        }

        void SetInstrumentationEnabled(
            bool enabled);

        bool TryGetInstrumentationSample(
            out SwarmRendererInstrumentationSample sample);

        SwarmRendererControlState GetControlState();
    }

    /// <summary>
    /// 씬 로드 직후 렌더러 참조를 한 번 찾고,
    /// 요청된 실험 Case에 맞춰 RMI/BRG 렌더러를 전환한다.
    ///
    /// 측정 시작 전에는 두 렌더러의 bounds, GPU 스키닝,
    /// 메시, 머터리얼 수, 분할 수, 컬링, 그림자,
    /// 모션 벡터, LOD 및 Layer 설정을 검증한다.
    /// </summary>
    public sealed class SwarmRendererInstrumentationReader
    {
        private SwarmRenderMeshIndirectRenderer
            _rmiRenderer;

        private SwarmBRGRenderer
            _brgRenderer;

        private SwarmInstancedRenderer
            _legacyRenderer;

        private bool
            _instrumentationEnabled;

        public bool InstrumentationEnabled =>
            _instrumentationEnabled;

        /// <summary>
        /// 씬의 렌더러 참조를 찾는다.
        /// 비활성 컴포넌트도 Case 전환 대상이므로 검색에 포함한다.
        /// </summary>
        public void RefreshSources()
        {
            _rmiRenderer =
                Object.FindFirstObjectByType<
                    SwarmRenderMeshIndirectRenderer>(
                    FindObjectsInactive.Include);

            _brgRenderer =
                Object.FindFirstObjectByType<
                    SwarmBRGRenderer>(
                    FindObjectsInactive.Include);

            _legacyRenderer =
                Object.FindFirstObjectByType<
                    SwarmInstancedRenderer>(
                    FindObjectsInactive.Include);
        }

        /// <summary>
        /// 요청 Case가 있으면 해당 백엔드만 활성화한다.
        /// Case가 null이면 씬에 설정된 활성 상태를 유지한다.
        ///
        /// 2x2 실험에서는 두 실험용 렌더러가 같은 씬에 존재해야 하며,
        /// 측정 시작 전에 실험 통제 조건이 일치하지 않으면 실행을 중단한다.
        /// </summary>
        public bool ConfigureForRun(
            SwarmRenderPipelineCase? requestedCase,
            bool instrumentationEnabled)
        {
            RefreshSources();

            _instrumentationEnabled =
                instrumentationEnabled;

            // 2x2 실험은 테스트 렌더러만 사용한다.
            // 기존 디버그 렌더러가 동시에 그리면 성능 수치와 화면 결과가 오염된다.
            if (_legacyRenderer != null)
            {
                _legacyRenderer.enabled = false;
            }

            if (!ValidateExperimentalControls(
                    out string validationError))
            {
                Debug.LogError(
                    "[Benchmark] RMI와 BRG의 실험 통제 조건이 일치하지 않습니다.\n" +
                    validationError);

                ApplyInstrumentationState();

                return false;
            }

            if (requestedCase.HasValue)
            {
                SwarmRenderPipelineCase renderCase =
                    requestedCase.Value;

                if (SwarmRenderPipelineCaseUtility
                        .IsRenderMeshIndirect(
                            renderCase))
                {
                    _rmiRenderer.SetRenderPipelineCase(
                        renderCase);

                    _brgRenderer
                        .SetInstrumentationEnabled(
                            false);

                    _brgRenderer.enabled =
                        false;

                    _rmiRenderer.enabled =
                        true;
                }
                else if (SwarmRenderPipelineCaseUtility
                             .IsBrg(
                                 renderCase))
                {
                    _brgRenderer.SetRenderPipelineCase(
                        renderCase);

                    _rmiRenderer
                        .SetInstrumentationEnabled(
                            false);

                    _rmiRenderer.enabled =
                        false;

                    _brgRenderer.enabled =
                        true;
                }
                else
                {
                    Debug.LogError(
                        "[Benchmark] 지원하지 않는 렌더 Case입니다: " +
                        renderCase);

                    ApplyInstrumentationState();

                    return false;
                }
            }

            ApplyInstrumentationState();

            return HasUsableRenderer(
                requestedCase);
        }

        /// <summary>
        /// 프레임 구조 계측 활성 상태를 변경한다.
        /// 렌더링 자체의 활성 상태에는 영향을 주지 않는다.
        /// </summary>
        public void SetInstrumentationEnabled(
            bool enabled)
        {
            _instrumentationEnabled =
                enabled;

            ApplyInstrumentationState();
        }

        /// <summary>
        /// 현재 활성화된 실험용 렌더러가 정확히 하나일 때
        /// 해당 렌더 Case를 반환한다.
        /// </summary>
        public bool TryResolveActiveCase(
            out SwarmRenderPipelineCase renderCase)
        {
            renderCase = default;

            bool rmiActive =
                _rmiRenderer != null &&
                _rmiRenderer.isActiveAndEnabled;

            bool brgActive =
                _brgRenderer != null &&
                _brgRenderer.isActiveAndEnabled;

            if (rmiActive == brgActive)
            {
                return false;
            }

            renderCase =
                rmiActive
                    ? _rmiRenderer
                        .InstrumentationCase
                    : _brgRenderer
                        .InstrumentationCase;

            return true;
        }

        /// <summary>
        /// 현재 프레임의 렌더러 구조 계측값을 읽는다.
        /// 측정 프레임 중에는 씬 검색이나 실험 조건 검증을 다시 수행하지 않는다.
        /// </summary>
        public bool TryCapture(
            SwarmRenderPipelineCase? expectedCase,
            out SwarmRendererInstrumentationSample sample)
        {
            sample = default;

            if (!_instrumentationEnabled)
            {
                return false;
            }

            if (expectedCase.HasValue)
            {
                SwarmRenderPipelineCase renderCase =
                    expectedCase.Value;

                if (SwarmRenderPipelineCaseUtility
                        .IsRenderMeshIndirect(
                            renderCase))
                {
                    return TryCaptureFrom(
                        _rmiRenderer,
                        renderCase,
                        out sample);
                }

                if (SwarmRenderPipelineCaseUtility
                        .IsBrg(
                            renderCase))
                {
                    return TryCaptureFrom(
                        _brgRenderer,
                        renderCase,
                        out sample);
                }

                return false;
            }

            bool rmiActive =
                _rmiRenderer != null &&
                _rmiRenderer.isActiveAndEnabled;

            bool brgActive =
                _brgRenderer != null &&
                _brgRenderer.isActiveAndEnabled;

            // 백엔드가 정확히 하나만 활성화된 경우에만 자동 선택한다.
            if (rmiActive == brgActive)
            {
                return false;
            }

            if (rmiActive)
            {
                return _rmiRenderer
                    .TryGetInstrumentationSample(
                        out sample);
            }

            return _brgRenderer
                .TryGetInstrumentationSample(
                    out sample);
        }

        private static bool TryCaptureFrom<T>(
            T renderer,
            SwarmRenderPipelineCase expectedCase,
            out SwarmRendererInstrumentationSample sample)
            where T :
                Behaviour,
                ISwarmRendererInstrumentationSource
        {
            sample = default;

            return
                renderer != null &&
                renderer.isActiveAndEnabled &&
                renderer.TryGetInstrumentationSample(
                    out sample) &&
                sample.IsValid &&
                sample.RenderCase ==
                expectedCase;
        }

        /// <summary>
        /// 현재 씬에서 2x2 실험에 필요한 두 렌더러가 존재하고,
        /// 독립변인 외 실험 통제 조건이 동일한지 검증한다.
        ///
        /// 셰이더 파일 자체의 Pass 구성은 이 메서드가 검사하지 않는다.
        /// 두 실험 셰이더는 Forward Pass 수와 Cull/ZWrite/ZTest를
        /// 별도로 동일하게 유지해야 한다.
        /// </summary>
        private bool ValidateExperimentalControls(
            out string error)
        {
            error = string.Empty;

            if (_rmiRenderer == null)
            {
                error =
                    "SwarmRenderMeshIndirectRenderer가 씬에 없습니다.";

                return false;
            }

            if (_brgRenderer == null)
            {
                error =
                    "SwarmBRGRenderer가 씬에 없습니다.";

                return false;
            }

            SwarmRendererControlState rmi =
                _rmiRenderer.GetControlState();

            SwarmRendererControlState brg =
                _brgRenderer.GetControlState();

            if (rmi.CullingMode !=
                brg.CullingMode)
            {
                error =
                    "컬링 모드 불일치: " +
                    $"RMI={rmi.CullingMode}, " +
                    $"BRG={brg.CullingMode}";

                return false;
            }

            if (!Approximately(
                    rmi.BoundsCenter,
                    brg.BoundsCenter))
            {
                error =
                    "Bounds Center 불일치: " +
                    $"RMI={rmi.BoundsCenter}, " +
                    $"BRG={brg.BoundsCenter}";

                return false;
            }

            if (!Approximately(
                    rmi.BoundsSize,
                    brg.BoundsSize))
            {
                error =
                    "Bounds Size 불일치: " +
                    $"RMI={rmi.BoundsSize}, " +
                    $"BRG={brg.BoundsSize}";

                return false;
            }

            if (!Mathf.Approximately(
                    rmi.ParticleScale,
                    brg.ParticleScale))
            {
                error =
                    "Particle Scale 불일치: " +
                    $"RMI={rmi.ParticleScale}, " +
                    $"BRG={brg.ParticleScale}";

                return false;
            }

            if (!Mathf.Approximately(
                    rmi.YOffset,
                    brg.YOffset))
            {
                error =
                    "Y Offset 불일치: " +
                    $"RMI={rmi.YOffset}, " +
                    $"BRG={brg.YOffset}";

                return false;
            }

            if (rmi.GpuSkinningRequested !=
                brg.GpuSkinningRequested)
            {
                error =
                    "GPU Skinning 요청 상태 불일치: " +
                    $"RMI={rmi.GpuSkinningRequested}, " +
                    $"BRG={brg.GpuSkinningRequested}";

                return false;
            }

            if (rmi.GpuSkinningActive !=
                brg.GpuSkinningActive)
            {
                error =
                    "실제 GPU Skinning 활성 상태 불일치: " +
                    $"RMI={rmi.GpuSkinningActive}, " +
                    $"BRG={brg.GpuSkinningActive}";

                return false;
            }

            if (rmi.GpuSkinningActive &&
                rmi.GpuSkinningClipSet !=
                brg.GpuSkinningClipSet)
            {
                error =
                    "GPU Skinning ClipSet 불일치.";

                return false;
            }

            if (rmi.RenderMesh == null ||
                brg.RenderMesh == null)
            {
                error =
                    "두 렌더러 중 하나의 실제 렌더 Mesh가 없습니다.";

                return false;
            }

            if (rmi.RenderMesh !=
                brg.RenderMesh)
            {
                error =
                    "실제 렌더 Mesh 불일치: " +
                    $"RMI={rmi.RenderMesh.name}, " +
                    $"BRG={brg.RenderMesh.name}";

                return false;
            }

            if (rmi.ActiveMaterialCount <= 0 ||
                brg.ActiveMaterialCount <= 0)
            {
                error =
                    "두 렌더러 중 하나에 유효한 머터리얼이 없습니다.";

                return false;
            }

            if (rmi.ActiveMaterialCount !=
                brg.ActiveMaterialCount)
            {
                error =
                    "유효 머터리얼 및 서브메시 수 불일치: " +
                    $"RMI={rmi.ActiveMaterialCount}, " +
                    $"BRG={brg.ActiveMaterialCount}";

                return false;
            }

            if (rmi.SplitCommandCount !=
                brg.SplitCommandCount)
            {
                error =
                    "Split 명령 수 불일치: " +
                    $"RMI={rmi.SplitCommandCount}, " +
                    $"BRG={brg.SplitCommandCount}";

                return false;
            }

            if (rmi.Layer !=
                brg.Layer)
            {
                error =
                    "GameObject Layer 불일치: " +
                    $"RMI={rmi.Layer}, " +
                    $"BRG={brg.Layer}";

                return false;
            }

            if (rmi.RenderingLayerMask !=
                brg.RenderingLayerMask)
            {
                error =
                    "Rendering Layer Mask 불일치: " +
                    $"RMI={rmi.RenderingLayerMask}, " +
                    $"BRG={brg.RenderingLayerMask}";

                return false;
            }

            if (rmi.ShadowCastingMode !=
                brg.ShadowCastingMode)
            {
                error =
                    "Shadow Casting Mode 불일치: " +
                    $"RMI={rmi.ShadowCastingMode}, " +
                    $"BRG={brg.ShadowCastingMode}";

                return false;
            }

            if (rmi.ReceiveShadows !=
                brg.ReceiveShadows)
            {
                error =
                    "Receive Shadows 설정 불일치: " +
                    $"RMI={rmi.ReceiveShadows}, " +
                    $"BRG={brg.ReceiveShadows}";

                return false;
            }

            if (rmi.MotionVectorMode !=
                brg.MotionVectorMode)
            {
                error =
                    "Motion Vector Mode 불일치: " +
                    $"RMI={rmi.MotionVectorMode}, " +
                    $"BRG={brg.MotionVectorMode}";

                return false;
            }

            if (rmi.ForcedMeshLod !=
                brg.ForcedMeshLod)
            {
                error =
                    "고정 Mesh LOD 불일치: " +
                    $"RMI={rmi.ForcedMeshLod}, " +
                    $"BRG={brg.ForcedMeshLod}";

                return false;
            }

            return true;
        }

        private bool HasUsableRenderer(
            SwarmRenderPipelineCase? requestedCase)
        {
            if (requestedCase.HasValue)
            {
                if (SwarmRenderPipelineCaseUtility
                        .IsRenderMeshIndirect(
                            requestedCase.Value))
                {
                    return
                        _rmiRenderer != null &&
                        _rmiRenderer
                            .isActiveAndEnabled;
                }

                if (SwarmRenderPipelineCaseUtility
                        .IsBrg(
                            requestedCase.Value))
                {
                    return
                        _brgRenderer != null &&
                        _brgRenderer
                            .isActiveAndEnabled;
                }

                return false;
            }

            bool rmiActive =
                _rmiRenderer != null &&
                _rmiRenderer.isActiveAndEnabled;

            bool brgActive =
                _brgRenderer != null &&
                _brgRenderer.isActiveAndEnabled;

            return rmiActive ^ brgActive;
        }

        private void ApplyInstrumentationState()
        {
            if (_rmiRenderer != null)
            {
                _rmiRenderer
                    .SetInstrumentationEnabled(
                        _instrumentationEnabled &&
                        _rmiRenderer
                            .isActiveAndEnabled);
            }

            if (_brgRenderer != null)
            {
                _brgRenderer
                    .SetInstrumentationEnabled(
                        _instrumentationEnabled &&
                        _brgRenderer
                            .isActiveAndEnabled);
            }
        }

        private static bool Approximately(
            Vector3 left,
            Vector3 right)
        {
            return
                Mathf.Approximately(
                    left.x,
                    right.x) &&
                Mathf.Approximately(
                    left.y,
                    right.y) &&
                Mathf.Approximately(
                    left.z,
                    right.z);
        }
    }
}
