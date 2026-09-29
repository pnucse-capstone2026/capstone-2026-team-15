using Unity.Entities;
using Unity.Mathematics;

namespace CameraCtrl
{
    /// <summary>
    /// 카메라 제어에 필요한 설정값과 런타임 상태를 저장하는 데이터 컴포넌트.
    /// </summary>
    public struct CameraRig : IComponentData
    {
        // --- 설정값 (인스펙터에서 지정) ---
        public float BaseDistance;        // 최저 각도(20도)일 때의 기본 거리
        public float HighAngleMultiplier; // 최고 각도(75도)일 때의 거리 배율
        public float PositionSmoothTime;  // 위치 이동 부드러움 (초 단위)
        public float RotationSpeed;       // 회전 보간 속도

        // --- 런타임 상태 (시스템이 제어) ---
        public Entity TargetEntity;       // 추적 대상 유닛 엔티티
        public float CurrentPitch;        // 현재 X축 회전 각도
        public float TargetPitch;         // 목표 X축 회전 각도
        public int PitchStepIndex;        // 현재 각도 단계 인덱스

        public float3 CurrentVelocity;    // SmoothDamp용 속도 캐시
        public bool IsInitialized;        // 초기화 완료 여부

        // --- 초기 회전값 보존 ---
        public float BaseYaw;
        public float BaseRoll;

        // [수정] 타겟을 부드럽게 추적하기 위한 가상 좌표
        public float3 CurrentFollowPosition;
    }
}