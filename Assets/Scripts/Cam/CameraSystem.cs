using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.InputSystem;
using Swarm;

namespace CameraCtrl
{
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    public partial class CameraSystem : SystemBase
    {
        // --- 설정 ---
        private readonly float[] _pitchSteps = { 20f, 30f, 45f, 60f, 75f };
        private float _nextScrollTime;
        private const float ScrollCooldown = 0.08f;

        // --- 디버그 설정 ---
        private bool _enableDebug = true;
        private float _debugLogTimer;

        private EntityQuery _targetQuery;

        protected override void OnCreate()
        {
            RequireForUpdate<CameraRig>();
            _targetQuery = SystemAPI.QueryBuilder()
                .WithAll<CameraFollowTargetTag, LocalTransform>()
                .Build();
        }

        protected override void OnUpdate()
        {
            var cam = UnityEngine.Camera.main;
            if (cam == null)
            {
                if (_enableDebug) Debug.LogWarning("[CameraSystem] MainCamera is NULL!");
                return;
            }

            // UnityEngine.Time 대신 SystemAPI 권장
            float dt = SystemAPI.Time.DeltaTime;

            foreach (var (rig, entity) in SystemAPI.Query<RefRW<CameraRig>>().WithEntityAccess())
            {
                ref var data = ref rig.ValueRW;

                // 1. 타겟 검색
                if (data.TargetEntity == Entity.Null || !SystemAPI.Exists(data.TargetEntity))
                {
                    if (!_targetQuery.IsEmpty)
                    {
                        var entities = _targetQuery.ToEntityArray(Allocator.Temp);
                        data.TargetEntity = entities[0];
                        if (_enableDebug) Debug.Log($"[CameraSystem] New Target Found: {data.TargetEntity}");
                    }
                    else
                    {
                        if (_enableDebug && UnityEngine.Time.unscaledTime > _debugLogTimer)
                            Debug.LogWarning("[CameraSystem] 타겟을 찾을 수 없습니다 (쿼리 비어있음)");
                        return;
                    }
                }

                var targetLt = SystemAPI.GetComponent<LocalTransform>(data.TargetEntity);
                float3 targetPos = targetLt.Position;

                // 2. 초기화
                if (!data.IsInitialized)
                {
                    var e = cam.transform.eulerAngles;
                    data.CurrentPitch = NormalizeAngle360(e.x);
                    data.BaseYaw = NormalizeAngle360(e.y);
                    data.BaseRoll = e.z;
                    data.PitchStepIndex = FindClosestStepIndex(data.CurrentPitch);
                    data.TargetPitch = _pitchSteps[data.PitchStepIndex];

                    // 최초 시작 시 카메라가 먼 곳에서 날아오지 않도록 위치 스냅
                    data.CurrentFollowPosition = targetPos;
                    data.IsInitialized = true;
                }

                // 3. 줌 입력
                float wheelY = (Mouse.current != null) ? Mouse.current.scroll.ReadValue().y : 0f;
                if (UnityEngine.Time.unscaledTime >= _nextScrollTime && math.abs(wheelY) > 0.01f)
                {
                    if (wheelY > 0) data.PitchStepIndex = math.min(data.PitchStepIndex + 1, _pitchSteps.Length - 1);
                    else data.PitchStepIndex = math.max(data.PitchStepIndex - 1, 0);

                    data.TargetPitch = _pitchSteps[data.PitchStepIndex];
                    _nextScrollTime = UnityEngine.Time.unscaledTime + ScrollCooldown;
                }

                // 4. 각도 계산
                float rotLerpT = 1f - math.exp(-data.RotationSpeed * dt);
                data.CurrentPitch = math.lerp(data.CurrentPitch, data.TargetPitch, rotLerpT);

                // ------------------------------------------------------------------
                // [수정된 쿼터뷰 로직]
                // ------------------------------------------------------------------

                // A. 가상 타겟(Follow Position) 부드럽게 이동
                // 사용자가 지정한 positionSmoothTime을 활용하여 프레임 독립적인 보간 수행
                // 향후 토러스 월드 적용 시, 이 부분에 순환 보정 로직이 들어가야 합니다.
                float smoothSpeed = 1f / math.max(data.PositionSmoothTime, 0.01f);
                float posLerpT = 1f - math.exp(-smoothSpeed * dt);
                data.CurrentFollowPosition = math.lerp(data.CurrentFollowPosition, targetPos, posLerpT);

                // B. 카메라 회전은 "무조건" 설정된 각도로 고정 (LookAt 미사용)
                quaternion fixedRotation = quaternion.Euler(
                    math.radians(data.CurrentPitch),
                    math.radians(data.BaseYaw),
                    math.radians(data.BaseRoll)
                );

                // C. 줌아웃 거리에 따른 오프셋 계산
                float minPitch = _pitchSteps[0];
                float maxPitch = _pitchSteps[_pitchSteps.Length - 1];
                float pitchRatio = math.unlerp(minPitch, maxPitch, data.CurrentPitch);
                float currentDist = math.lerp(data.BaseDistance, data.BaseDistance * data.HighAngleMultiplier, pitchRatio);

                // D. 최종 카메라 좌표 산출 (추적 좌표에서 카메라가 바라보는 반대 방향으로 거리만큼 뺌)
                float3 backwardOffset = math.mul(fixedRotation, new float3(0, 0, -currentDist));
                float3 finalCamPos = data.CurrentFollowPosition + backwardOffset;

                // 적용
                cam.transform.position = finalCamPos;
                cam.transform.rotation = fixedRotation;

                // ------------------------------------------------------------------
                // [DEBUG] 시각화 및 로그
                // ------------------------------------------------------------------
                if (_enableDebug)
                {
                    Debug.DrawLine(targetPos, targetPos + new float3(0, 5, 0), Color.green); // 실제 타겟
                    Debug.DrawLine(data.CurrentFollowPosition, data.CurrentFollowPosition + new float3(0, 5, 0), Color.red); // 가상 추적점
                    Debug.DrawLine(finalCamPos, data.CurrentFollowPosition, Color.yellow); // 카메라 -> 추적점 연결선

                    if (UnityEngine.Time.unscaledTime > _debugLogTimer)
                    {
                        Debug.Log($"[CamDebug]\n" +
                                  $"Target: {targetPos}\n" +
                                  $"FollowPos: {data.CurrentFollowPosition}\n" +
                                  $"CamPos: {finalCamPos}\n" +
                                  $"Dist: {currentDist}");

                        _debugLogTimer = UnityEngine.Time.unscaledTime + 0.5f;
                    }
                }
            }
        }

        private float NormalizeAngle360(float deg)
        {
            float angle = deg % 360f;
            if (angle < 0f) angle += 360f;
            return angle;
        }

        private int FindClosestStepIndex(float pitch)
        {
            int best = 0;
            float minDiff = float.MaxValue;
            for (int i = 0; i < _pitchSteps.Length; i++)
            {
                float diff = math.abs(pitch - _pitchSteps[i]);
                if (diff < minDiff) { minDiff = diff; best = i; }
            }
            return best;
        }
    }
}