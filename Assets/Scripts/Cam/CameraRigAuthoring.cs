using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace CameraCtrl
{
    public class CameraRigAuthoring : MonoBehaviour
    {
        [Header("Distance Strategy")]
        [Tooltip("가장 낮은 각도(20도)에서의 기본 거리")]
        public float baseDistance = 15f;

        [Tooltip("가장 높은 각도(75도)에서 거리가 몇 배 멀어질지 (예: 1.5 = 1.5배)")]
        public float highAngleMultiplier = 1.6f;

        [Header("Smoothness")]
        [Tooltip("위치 추적 지연 시간 (0.05: 빠름 ~ 0.2: 부드러움)")]
        public float positionSmoothTime = 0.08f;

        [Tooltip("각도 회전 속도")]
        public float rotationSpeed = 6f;

        class Baker : Baker<CameraRigAuthoring>
        {
            public override void Bake(CameraRigAuthoring authoring)
            {
                // 카메라는 좌표가 필요 없는 데이터 엔티티이므로 TransformUsageFlags.None 사용
                var entity = GetEntity(TransformUsageFlags.None);

                AddComponent(entity, new CameraRig
                {
                    BaseDistance = authoring.baseDistance,
                    HighAngleMultiplier = authoring.highAngleMultiplier,
                    PositionSmoothTime = authoring.positionSmoothTime,
                    RotationSpeed = authoring.rotationSpeed,

                    // 초기 상태값 설정
                    IsInitialized = false,
                    CurrentPitch = 45f,
                    TargetEntity = Entity.Null,
                    CurrentVelocity = float3.zero,
                    CurrentFollowPosition = float3.zero // 초기화 추가
                });
            }
        }
    }
}