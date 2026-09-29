using Unity.Entities;
using Unity.Mathematics;

namespace Detection
{
    public struct GlobalDetectionConfig : IComponentData
    {
        public float CellSize;
        public float ObstaclePenaltyWeight; // 장애물 뒤의 적에게 부여할 거리 페널티 (기본값 추천: 2.0)
    }

    public struct DetectionTag : IComponentData
    {
        public int FactionId;
        public bool IsBase;
        public float SearchRange;
        public float TauntWeight;           // 적이 나를 얼마나 우선적으로 공격할지 (도발치)
        public float TargetTauntThreshold; // 이 수치 이상의 적을 발견하면 최우선 타겟팅
    }

    public struct DetectionTarget : IComponentData
    {
        public Entity CenterTarget;
        public float3 TargetPos;
        public bool HasTarget;
    }

    // 차폐 구현을 위한 장애물 태그
    public struct ObstacleTag : IComponentData
    {
        public float Penalty; // 해당 장애물 근처의 적 탐색 시 부여할 가중치
    }
}