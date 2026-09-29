using Unity.Entities;
using Unity.Mathematics;

namespace Swarm
{
    // ==========================================================
    //  Legion Base / Unit Container
    // ==========================================================

    /// <summary>
    /// Legion Base 엔티티임을 표시하는 태그 컴포넌트.
    /// Base는 유닛을 생성하거나 보유하고, 생성된 유닛에게 이동 좌표를 지시하는 상위 객체이다.
    /// </summary>
    public struct LegionBaseTag : IComponentData
    {
    }

    /// <summary>
    /// Legion Base의 현재 상태.
    /// </summary>
    public enum LegionBaseState : byte
    {
        /// <summary>
        /// 대기 상태.
        /// </summary>
        Idle = 0,

        /// <summary>
        /// 유닛 생성 중.
        /// </summary>
        Spawning = 1,

        /// <summary>
        /// 유닛 생성 및 이동 지시 완료.
        /// </summary>
        Deployed = 2
    }

    /// <summary>
    /// Legion Base의 핵심 설정 및 런타임 상태.
    /// </summary>
    public struct LegionBaseData : IComponentData
    {
        /// <summary>
        /// Base가 씬 시작 시 자동으로 유닛을 생성할지 여부.
        /// </summary>
        public bool SpawnOnStart;

        /// <summary>
        /// 이미 유닛 생성을 완료했는지 여부.
        /// 1회 생성 방지용 플래그.
        /// </summary>
        public bool Spawned;

        /// <summary>
        /// 생성할 유닛 수.
        /// </summary>
        public int SpawnCount;

        /// <summary>
        /// 현재까지 생성한 유닛 수.
        /// </summary>
        public int SpawnedCount;

        /// <summary>
        /// Base 기준 유닛 생성 위치 오프셋.
        /// 예: (0, 0, -2)이면 Base 뒤쪽에서 생성.
        /// </summary>
        public float3 SpawnOffset;

        /// <summary>
        /// 생성된 유닛들이 이동할 기본 목표 좌표.
        /// 월드 좌표 기준.
        /// </summary>
        public float3 DeployTargetPosition;

        /// <summary>
        /// 여러 유닛을 생성할 때 목표 지점이 겹치지 않도록 벌리는 간격.
        /// </summary>
        public float DeploySpacing;

        /// <summary>
        /// 여러 유닛을 생성할 때 한 줄에 배치할 유닛 수.
        /// </summary>
        public int DeployColumns;

        /// <summary>
        /// Base의 현재 상태.
        /// </summary>
        public LegionBaseState State;
    }

    /// <summary>
    /// Base가 생성한 Unit의 설정을 덮어쓰기 위한 데이터.
    /// Unit Prefab의 기본 DetectionTag / UnitAttackParams 값을 Base 기준으로 override한다.
    /// </summary>
    public struct LegionBaseUnitOverrideData : IComponentData
    {
        /// <summary>
        /// 생성된 Unit의 DetectionTag를 Base 설정으로 덮어쓸지 여부.
        /// </summary>
        public bool OverrideDetection;

        /// <summary>
        /// 생성된 Unit에 적용할 Faction ID.
        /// 서로 다른 Base가 같은 Unit Prefab을 공유하더라도 이 값으로 진영을 분리할 수 있다.
        /// </summary>
        public int FactionId;

        /// <summary>
        /// 생성된 Unit의 DetectionTag.IsBase 값.
        /// 일반 전투 유닛이면 false 권장.
        /// </summary>
        public bool IsBaseUnit;

        /// <summary>
        /// 생성된 Unit의 탐지 범위.
        /// </summary>
        public float SearchRange;

        /// <summary>
        /// 생성된 Unit의 도발 가중치.
        /// </summary>
        public float TauntWeight;

        /// <summary>
        /// 생성된 Unit의 타겟 도발 임계값.
        /// </summary>
        public float TargetTauntThreshold;

        /// <summary>
        /// 생성된 Unit의 UnitAttackParams를 Base 설정으로 덮어쓸지 여부.
        /// </summary>
        public bool OverrideAttack;

        /// <summary>
        /// 생성된 Unit의 공격 사거리.
        /// </summary>
        public float AttackRange;

        /// <summary>
        /// 생성된 Unit의 공격 주기.
        /// </summary>
        public float AttackCooldown;
    }

    /// <summary>
    /// Legion Base가 생성할 Unit Prefab Entity.
    /// Baker에서 GameObject Prefab을 Entity Prefab으로 변환하여 저장한다.
    /// </summary>
    public struct LegionUnitPrefab : IComponentData
    {
        public Entity Prefab;
    }

    /// <summary>
    /// 자동 실행/벤치마크에서 선택할 수 있는 Unit Prefab 후보.
    /// 인덱스는 BenchmarkConfig.swarmTypes / spawnCases의 타입 인덱스와 맞춘다.
    /// </summary>
    [InternalBufferCapacity(8)]
    public struct LegionUnitPrefabOption : IBufferElementData
    {
        public Entity Prefab;
    }

    /// <summary>
    /// Legion Base가 보유하거나 생성한 Unit 목록.
    /// Base와 Unit의 소유 관계를 추적하기 위한 DynamicBuffer.
    /// </summary>
    [InternalBufferCapacity(32)]
    public struct LegionBaseUnitElement : IBufferElementData
    {
        /// <summary>
        /// 생성된 Unit Entity.
        /// </summary>
        public Entity UnitEntity;

        /// <summary>
        /// Base 내부에서의 Unit 인덱스.
        /// </summary>
        public int Index;

        /// <summary>
        /// 이 Unit이 이동해야 할 최종 배치 좌표.
        /// 월드 좌표 기준.
        /// </summary>
        public float3 DeployTargetPosition;
    }
}
