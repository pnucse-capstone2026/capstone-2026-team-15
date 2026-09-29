using Swarm;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public class LegionBaseAuthoring : MonoBehaviour
{
    [Header("Unit Prefab")]
    [Tooltip("Base에서 생성할 Unit Prefab. 해당 Prefab에는 UnitAuthoring이 붙어 있어야 합니다.")]
    [SerializeField] private GameObject unitPrefab;

    [Tooltip("벤치마크 자동화에서 선택할 Unit Prefab 후보입니다. 비어 있으면 unitPrefab만 후보로 사용합니다.")]
    [SerializeField] private GameObject[] swarmTypePrefabs = new GameObject[0];

    [Header("Spawn Settings")]
    [Tooltip("씬 시작 시 자동으로 유닛을 생성할지 여부입니다.")]
    [SerializeField] private bool spawnOnStart = true;

    [Tooltip("생성할 유닛 수입니다.")]
    [Min(0)]
    [SerializeField] private int spawnCount = 1;

    [Tooltip("Base 위치 기준 유닛 생성 오프셋입니다.")]
    [SerializeField] private Vector3 spawnOffset = new Vector3(0f, 0f, -2f);

    [Header("Deploy Settings")]
    [Tooltip("생성된 유닛들이 이동할 월드 좌표입니다.")]
    [SerializeField] private Vector3 deployTargetPosition = new Vector3(10f, 0f, 10f);

    [Tooltip("여러 유닛을 배치할 때 목표 좌표 간 간격입니다.")]
    [Min(0f)]
    [SerializeField] private float deploySpacing = 3f;

    [Tooltip("여러 유닛을 배치할 때 한 줄에 배치할 유닛 수입니다.")]
    [Min(1)]
    [SerializeField] private int deployColumns = 4;

    [Header("Unit Override - Detection")]
    [Tooltip("생성된 Unit의 DetectionTag를 Base 설정으로 덮어쓸지 여부입니다.")]
    [SerializeField] private bool overrideDetection = true;

    [Tooltip("Base가 생성한 Unit에 적용할 Faction ID입니다.")]
    [SerializeField] private int unitFactionId = 0;

    [Tooltip("생성된 Unit을 Base로 취급할지 여부입니다. 일반 전투 유닛이면 false가 적절합니다.")]
    [SerializeField] private bool spawnedUnitIsBase = false;

    [Tooltip("생성된 Unit의 탐지 범위입니다.")]
    [Min(0f)]
    [SerializeField] private float unitSearchRange = 20f;

    [Tooltip("생성된 Unit의 도발 가중치입니다.")]
    [SerializeField] private float unitTauntWeight = 1f;

    [Tooltip("생성된 Unit의 타겟 도발 임계값입니다.")]
    [SerializeField] private float unitTargetTauntThreshold = 5f;

    [Header("Unit Override - Attack")]
    [Tooltip("생성된 Unit의 공격 파라미터를 Base 설정으로 덮어쓸지 여부입니다.")]
    [SerializeField] private bool overrideAttack = true;

    [Tooltip("생성된 Unit의 공격 사거리입니다.")]
    [Min(0f)]
    [SerializeField] private float unitAttackRange = 4f;

    [Tooltip("생성된 Unit의 공격 주기입니다.")]
    [Min(0f)]
    [SerializeField] private float unitAttackCooldown = 1.4f;

    class Baker : Baker<LegionBaseAuthoring>
    {
        public override void Bake(LegionBaseAuthoring authoring)
        {
            Entity entity = GetEntity(
                TransformUsageFlags.Dynamic | TransformUsageFlags.Renderable);

            AddComponent<LegionBaseTag>(entity);

            AddComponent(entity, new LegionBaseData
            {
                SpawnOnStart = authoring.spawnOnStart,
                Spawned = false,
                SpawnCount = math.max(0, authoring.spawnCount),
                SpawnedCount = 0,

                SpawnOffset = new float3(
                    authoring.spawnOffset.x,
                    authoring.spawnOffset.y,
                    authoring.spawnOffset.z),

                DeployTargetPosition = new float3(
                    authoring.deployTargetPosition.x,
                    authoring.deployTargetPosition.y,
                    authoring.deployTargetPosition.z),

                DeploySpacing = math.max(0f, authoring.deploySpacing),
                DeployColumns = math.max(1, authoring.deployColumns),

                State = LegionBaseState.Idle
            });

            AddComponent(entity, new LegionBaseUnitOverrideData
            {
                OverrideDetection = authoring.overrideDetection,
                FactionId = authoring.unitFactionId,
                IsBaseUnit = authoring.spawnedUnitIsBase,
                SearchRange = math.max(0f, authoring.unitSearchRange),
                TauntWeight = authoring.unitTauntWeight,
                TargetTauntThreshold = authoring.unitTargetTauntThreshold,

                OverrideAttack = authoring.overrideAttack,
                AttackRange = math.max(0f, authoring.unitAttackRange),
                AttackCooldown = math.max(0f, authoring.unitAttackCooldown)
            });

            Entity prefabEntity = Entity.Null;

            if (authoring.unitPrefab != null)
            {
                prefabEntity = GetEntity(
                    authoring.unitPrefab,
                    TransformUsageFlags.Dynamic | TransformUsageFlags.Renderable);
            }

            AddComponent(entity, new LegionUnitPrefab
            {
                Prefab = prefabEntity
            });

            DynamicBuffer<LegionUnitPrefabOption> prefabOptions =
                AddBuffer<LegionUnitPrefabOption>(entity);

            if (authoring.swarmTypePrefabs != null &&
                authoring.swarmTypePrefabs.Length > 0)
            {
                foreach (GameObject prefab in authoring.swarmTypePrefabs)
                {
                    if (prefab == null)
                    {
                        prefabOptions.Add(new LegionUnitPrefabOption
                        {
                            Prefab = Entity.Null
                        });
                        continue;
                    }

                    prefabOptions.Add(new LegionUnitPrefabOption
                    {
                        Prefab = GetEntity(
                            prefab,
                            TransformUsageFlags.Dynamic | TransformUsageFlags.Renderable)
                    });
                }
            }
            else if (prefabEntity != Entity.Null)
            {
                prefabOptions.Add(new LegionUnitPrefabOption
                {
                    Prefab = prefabEntity
                });
            }

            AddBuffer<LegionBaseUnitElement>(entity);
        }
    }
}
