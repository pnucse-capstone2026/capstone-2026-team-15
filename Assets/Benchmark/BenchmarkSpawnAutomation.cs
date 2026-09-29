using System.Collections;
using System.Collections.Generic;
using Detection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Swarm.Benchmark
{
    internal sealed class BenchmarkSpawnAutomation
    {
        private readonly BenchmarkConfig _config;
        private readonly int _caseIndex;
        private readonly bool _useWorkload;
        private readonly BenchmarkWorkload _workload;
        private BenchmarkSpawnEntry[] _appliedEntries;

        public SpawnMetadata Metadata { get; private set; }

        private bool AutomationActive => _useWorkload || _config.HasSpawnAutomation;

        public bool IsActive => AutomationActive;

        public string Label => _useWorkload
            ? $"N{_workload.totalInstances}_L{_workload.swarmPartition}:{BuildComposition()}"
            : (_config.HasSpawnAutomation
                ? $"{_config.GetSanitizedSpawnCase(_caseIndex).label}:{BuildComposition()}"
                : "scene-default");

        public BenchmarkSpawnAutomation(BenchmarkConfig config, int caseIndex)
        {
            _config = config;
            _caseIndex = caseIndex;
            Metadata = CreateMetadata();
        }

        public BenchmarkSpawnAutomation(BenchmarkConfig config, BenchmarkWorkload workload)
        {
            _config = config;
            _caseIndex = 0;
            _useWorkload = true;
            _workload = workload;
            Metadata = CreateMetadata();
        }

        public bool TryApply(out int appliedBaseCount)
        {
            appliedBaseCount = 0;
            if (!AutomationActive)
                return true;

            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
                return false;

            EntityManager entityManager = world.EntityManager;
            EntityQuery query = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<LegionBaseTag>(),
                ComponentType.ReadWrite<LegionBaseData>(),
                ComponentType.ReadWrite<LegionUnitPrefab>(),
                ComponentType.ReadWrite<LegionBaseUnitElement>());

            using NativeArray<Entity> bases = query.ToEntityArray(Allocator.Temp);
            query.Dispose();
            if (bases.Length == 0)
                return false;

            BenchmarkSpawnEntry[] entries = ResolveEntries(bases.Length);
            _appliedEntries = entries;
            ClearAllRuntimeUnits(entityManager);

            foreach (Entity baseEntity in bases)
            {
                if (!entityManager.Exists(baseEntity))
                    continue;

                ClearSpawnedUnits(entityManager, baseEntity);
                LegionBaseData baseData = entityManager.GetComponentData<LegionBaseData>(baseEntity);
                baseData.SpawnOnStart = true;
                baseData.Spawned = false;
                baseData.SpawnCount = SumSpawnCount(entries);
                baseData.SpawnedCount = 0;
                baseData.State = LegionBaseState.Idle;
                entityManager.SetComponentData(baseEntity, baseData);
                entityManager.GetBuffer<LegionBaseUnitElement>(baseEntity).Clear();

                float placementSpacing = GetPlacementSpacing(
                    entityManager, baseEntity, baseData, entries);
                int spawnedForBase = 0;

                foreach (BenchmarkSpawnEntry entry in entries)
                {
                    if (!TryGetPrefab(entityManager, baseEntity, entry.swarmTypeIndex, out Entity prefab))
                        prefab = entityManager.GetComponentData<LegionUnitPrefab>(baseEntity).Prefab;

                    if (prefab == Entity.Null)
                        continue;

                    entityManager.SetComponentData(baseEntity, new LegionUnitPrefab { Prefab = prefab });
                    spawnedForBase += SpawnUnits(
                        entityManager, baseEntity, prefab, baseData,
                        entry.spawnCount, entry.soldierCount,
                        spawnedForBase, placementSpacing);
                }

                baseData.Spawned = true;
                baseData.SpawnedCount = spawnedForBase;
                baseData.State = LegionBaseState.Deployed;
                entityManager.SetComponentData(baseEntity, baseData);
                appliedBaseCount++;
            }

            Debug.Log($"[Benchmark] Applied spawn case '{Label}' to {appliedBaseCount} base(s).");
            return appliedBaseCount > 0;
        }

        public IEnumerator WaitForResult(int appliedBaseCount)
        {
            BenchmarkSpawnEntry[] entries =
                _appliedEntries ?? ResolveEntries(Mathf.Max(1, appliedBaseCount));
            int expected = SumSpawnCount(entries) * Mathf.Max(0, appliedBaseCount);

            for (int attempt = 0; attempt < 120; attempt++)
            {
                SpawnVerification verification = GetVerification();
                if (Matches(entries, expected, verification))
                {
                    Capture(expected, verification, true);
                    Debug.Log(
                        $"[Benchmark] Spawn verification expectedUnits={expected} " +
                        $"actualUnits={verification.UnitEntities} melee={verification.MeleeUnits} " +
                        $"archer={verification.ArcherUnits} soldiers={verification.Soldiers}");
                    yield break;
                }
                yield return null;
            }

            SpawnVerification final = GetVerification();
            Capture(expected, final, false);
            Debug.LogWarning(
                $"[Benchmark] Spawn verification timed out expectedUnits={expected} " +
                $"actualUnits={final.UnitEntities}");
        }

        public void ApplyMetadata(RunSummary summary)
        {
            SpawnMetadata value = Metadata;
            summary.spawnCaseLabel = value.CaseLabel;
            summary.spawnTypeIndex = value.TypeIndex;
            summary.spawnTypeId = value.TypeId;
            summary.spawnTypeLabel = value.TypeLabel;
            summary.spawnCount = value.Count;
            summary.spawnComposition = value.Composition;
            summary.spawnExpectedUnits = value.ExpectedUnits;
            summary.spawnActualUnits = value.ActualUnits;
            summary.spawnMeleeUnits = value.MeleeUnits;
            summary.spawnArcherUnits = value.ArcherUnits;
            summary.spawnSoldiers = value.Soldiers;
            summary.spawnVerificationMatched = value.VerificationMatched;
            summary.totalInstances = value.TotalInstances;
            summary.swarmPartition = value.SwarmPartition;
            summary.soldiersPerSwarm = value.SwarmPartition > 0
                ? value.TotalInstances / value.SwarmPartition
                : 0;
        }

        private SpawnMetadata CreateMetadata()
        {
            if (_useWorkload)
            {
                int n = _workload.totalInstances;
                int l = _workload.swarmPartition;
                return new SpawnMetadata
                {
                    CaseLabel = $"N{n}_L{l}", TypeIndex = -1,
                    TypeId = "melee+archer", TypeLabel = "melee+archer",
                    Count = l, Composition = BuildComposition(),
                    TotalInstances = n, SwarmPartition = l,
                    ExpectedUnits = -1, ActualUnits = -1,
                    MeleeUnits = -1, ArcherUnits = -1, Soldiers = -1
                };
            }

            if (!_config.HasSpawnAutomation)
            {
                return new SpawnMetadata
                {
                    CaseLabel = "scene-default", TypeIndex = -1,
                    Count = -1, ExpectedUnits = -1, ActualUnits = -1,
                    MeleeUnits = -1, ArcherUnits = -1, Soldiers = -1,
                    TotalInstances = -1, SwarmPartition = -1
                };
            }

            BenchmarkSpawnCase spawnCase = _config.GetSanitizedSpawnCase(_caseIndex);
            BenchmarkSpawnEntry[] entries = _config.GetSanitizedSpawnEntries(_caseIndex);
            var ids = new List<string>(entries.Length);
            var labels = new List<string>(entries.Length);
            foreach (BenchmarkSpawnEntry entry in entries)
            {
                BenchmarkSwarmType type = _config.GetSanitizedSwarmType(entry.swarmTypeIndex);
                ids.Add(type.id);
                labels.Add(type.label);
            }

            return new SpawnMetadata
            {
                CaseLabel = spawnCase.label,
                TypeIndex = entries.Length == 1 ? entries[0].swarmTypeIndex : -1,
                TypeId = string.Join("+", ids),
                TypeLabel = string.Join("+", labels),
                Count = SumSpawnCount(entries),
                Composition = BuildComposition(),
                TotalInstances = -1, SwarmPartition = -1
            };
        }

        private string BuildComposition()
        {
            if (_useWorkload)
            {
                int l = _workload.swarmPartition;
                int melee = l / 2;
                int ranged = l - melee;
                return $"melee{melee}+archer{ranged}@soldiers{_workload.SoldiersPerSwarm}";
            }

            if (!_config.HasSpawnAutomation)
                return "scene-default";

            BenchmarkSpawnEntry[] entries = _config.GetSanitizedSpawnEntries(_caseIndex);
            var parts = new List<string>(entries.Length);
            foreach (BenchmarkSpawnEntry entry in entries)
            {
                BenchmarkSwarmType type = _config.GetSanitizedSwarmType(entry.swarmTypeIndex);
                string soldiers = entry.soldierCount > 0
                    ? entry.soldierCount.ToString()
                    : "Prefab";
                parts.Add($"{type.id}:count{entry.spawnCount}:soldiers{soldiers}");
            }
            return string.Join("+", parts);
        }

        private void Capture(int expected, SpawnVerification verification, bool matched)
        {
            SpawnMetadata value = Metadata;
            value.ExpectedUnits = expected;
            value.ActualUnits = verification.UnitEntities;
            value.MeleeUnits = verification.MeleeUnits;
            value.ArcherUnits = verification.ArcherUnits;
            value.Soldiers = verification.Soldiers;
            value.VerificationMatched = matched;
            Metadata = value;
        }

        private static int SumSpawnCount(BenchmarkSpawnEntry[] entries)
        {
            int total = 0;
            foreach (BenchmarkSpawnEntry entry in entries)
                total += Mathf.Max(0, entry.spawnCount);
            return total;
        }

        private BenchmarkSpawnEntry[] ResolveEntries(int baseCount)
        {
            if (_useWorkload)
                return BuildWorkloadEntries(Mathf.Max(1, baseCount));
            return _config.GetSanitizedSpawnEntries(_caseIndex);
        }

        /// <summary>
        /// (N,L)을 팩션(base)당 근접/원거리 엔트리로 변환한다.
        /// 총 근접=L/2, 총 원거리=L/2, 스웜당 병사=N/L. baseCount로 균등 분배.
        /// </summary>
        private BenchmarkSpawnEntry[] BuildWorkloadEntries(int baseCount)
        {
            int l = Mathf.Max(0, _workload.swarmPartition);
            int totalMelee = l / 2;
            int totalRanged = l - totalMelee;
            int soldiers = _workload.SoldiersPerSwarm;

            if (totalMelee % baseCount != 0 || totalRanged % baseCount != 0)
            {
                Debug.LogWarning(
                    $"[Benchmark] 워크로드 L={l}이 baseCount={baseCount}로 균등 분배되지 " +
                    "않아 팩션당 스웜 수를 내림 처리합니다.");
            }

            return new[]
            {
                new BenchmarkSpawnEntry
                {
                    swarmTypeIndex = _config.meleeSwarmTypeIndex,
                    spawnCount = totalMelee / baseCount,
                    soldierCount = soldiers
                },
                new BenchmarkSpawnEntry
                {
                    swarmTypeIndex = _config.rangedSwarmTypeIndex,
                    spawnCount = totalRanged / baseCount,
                    soldierCount = soldiers
                }
            };
        }

        private static float GetPlacementSpacing(
            EntityManager manager, Entity baseEntity, LegionBaseData baseData,
            BenchmarkSpawnEntry[] entries)
        {
            float result = Mathf.Max(0f, baseData.DeploySpacing);
            float scale = manager.GetComponentData<LocalTransform>(baseEntity).Scale;
            foreach (BenchmarkSpawnEntry entry in entries)
            {
                if (!TryGetPrefab(manager, baseEntity, entry.swarmTypeIndex, out Entity prefab) ||
                    !manager.HasComponent<SwarmParams>(prefab))
                    continue;

                SwarmParams parameters = manager.GetComponentData<SwarmParams>(prefab);
                if (entry.soldierCount > 0)
                    parameters.SoldierCount = entry.soldierCount;
                result = LegionSpawnPlacement.GetCenterSpacing(in parameters, result, scale);
            }
            return result;
        }

        private static int SpawnUnits(
            EntityManager manager, Entity baseEntity, Entity prefab,
            LegionBaseData baseData, int requestedCount, int requestedSoldiers,
            int startIndex, float spacing)
        {
            int count = Mathf.Max(0, requestedCount);
            LocalTransform baseTransform = manager.GetComponentData<LocalTransform>(baseEntity);
            LegionBaseUnitOverrideData unitOverride =
                manager.HasComponent<LegionBaseUnitOverrideData>(baseEntity)
                    ? manager.GetComponentData<LegionBaseUnitOverrideData>(baseEntity)
                    : default;

            for (int i = 0; i < count; i++)
            {
                int index = startIndex + i;
                Entity unit = manager.Instantiate(prefab);
                float3 offset = GetGridOffset(index, Mathf.Max(1, baseData.DeployColumns), spacing);
                float3 spawnPosition = baseTransform.Position +
                    math.rotate(baseTransform.Rotation, baseData.SpawnOffset + offset);
                float3 target = baseTransform.Position +
                    math.rotate(baseTransform.Rotation, baseData.DeployTargetPosition + offset);

                manager.SetComponentData(unit, new LocalTransform
                {
                    Position = spawnPosition,
                    Rotation = baseTransform.Rotation,
                    Scale = baseTransform.Scale
                });

                if (manager.HasComponent<UnitMoveTarget>(unit))
                    manager.SetComponentData(unit, new UnitMoveTarget { HasTarget = true, Position = target });

                if (unitOverride.OverrideDetection && manager.HasComponent<DetectionTag>(unit))
                    manager.SetComponentData(unit, new DetectionTag
                    {
                        FactionId = unitOverride.FactionId,
                        IsBase = unitOverride.IsBaseUnit,
                        SearchRange = unitOverride.SearchRange,
                        TauntWeight = unitOverride.TauntWeight,
                        TargetTauntThreshold = unitOverride.TargetTauntThreshold
                    });

                if (unitOverride.OverrideAttack && manager.HasComponent<UnitAttackParams>(unit))
                    manager.SetComponentData(unit, new UnitAttackParams
                    {
                        AttackRange = unitOverride.AttackRange,
                        AttackCooldown = unitOverride.AttackCooldown
                    });

                if (requestedSoldiers > 0 && manager.HasComponent<SwarmParams>(unit))
                {
                    SwarmParams parameters = manager.GetComponentData<SwarmParams>(unit);
                    parameters.SoldierCount = Mathf.Max(1, requestedSoldiers);
                    manager.SetComponentData(unit, parameters);
                }

                manager.GetBuffer<LegionBaseUnitElement>(baseEntity).Add(
                    new LegionBaseUnitElement
                    {
                        UnitEntity = unit, Index = index, DeployTargetPosition = target
                    });
            }
            return count;
        }

        private static float3 GetGridOffset(int index, int columns, float spacing)
        {
            int row = index / columns;
            int column = index % columns;
            return new float3((column - (columns - 1) * 0.5f) * spacing, 0f, row * spacing);
        }

        private static void ClearAllRuntimeUnits(EntityManager manager)
        {
            EntityQuery query = manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<UnitTag>() },
                None = new[] { ComponentType.ReadOnly<Prefab>() }
            });
            using NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            query.Dispose();
            if (entities.Length > 0)
                manager.DestroyEntity(entities);
        }

        private static void ClearSpawnedUnits(EntityManager manager, Entity baseEntity)
        {
            DynamicBuffer<LegionBaseUnitElement> buffer =
                manager.GetBuffer<LegionBaseUnitElement>(baseEntity);
            for (int i = 0; i < buffer.Length; i++)
            {
                Entity unit = buffer[i].UnitEntity;
                if (unit != Entity.Null && manager.Exists(unit))
                    manager.DestroyEntity(unit);
            }
            buffer.Clear();
        }

        private static bool TryGetPrefab(
            EntityManager manager, Entity baseEntity, int typeIndex, out Entity prefab)
        {
            prefab = Entity.Null;
            if (!manager.HasBuffer<LegionUnitPrefabOption>(baseEntity))
                return false;
            DynamicBuffer<LegionUnitPrefabOption> options =
                manager.GetBuffer<LegionUnitPrefabOption>(baseEntity);
            if (options.Length == 0)
                return false;
            prefab = options[Mathf.Clamp(typeIndex, 0, options.Length - 1)].Prefab;
            return prefab != Entity.Null;
        }

        private static SpawnVerification GetVerification()
        {
            var result = new SpawnVerification();
            World world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated)
                return result;
            EntityManager manager = world.EntityManager;
            EntityQuery query = manager.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<UnitTag>(),
                    ComponentType.ReadOnly<UnitRoleComponent>()
                },
                None = new[] { ComponentType.ReadOnly<Prefab>() }
            });
            using NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            query.Dispose();
            result.UnitEntities = entities.Length;
            foreach (Entity entity in entities)
            {
                UnitRole role = manager.GetComponentData<UnitRoleComponent>(entity).Value;
                if (role == UnitRole.MeleeInfantry) result.MeleeUnits++;
                else if (role == UnitRole.Archer) result.ArcherUnits++;
                if (manager.HasComponent<SwarmParams>(entity))
                    result.Soldiers += Mathf.Max(
                        0, manager.GetComponentData<SwarmParams>(entity).SoldierCount);
            }
            return result;
        }

        private static bool Matches(
            BenchmarkSpawnEntry[] entries, int expected, SpawnVerification actual)
        {
            if (actual.UnitEntities != expected)
                return false;
            if (expected == 0)
                return actual.MeleeUnits == 0 && actual.ArcherUnits == 0;

            int perBase = SumSpawnCount(entries);
            int baseCount = perBase > 0 ? expected / perBase : 0;
            int melee = 0;
            int archer = 0;
            foreach (BenchmarkSpawnEntry entry in entries)
            {
                if (entry.swarmTypeIndex == 0) melee += entry.spawnCount * baseCount;
                else if (entry.swarmTypeIndex == 1) archer += entry.spawnCount * baseCount;
            }
            return actual.MeleeUnits == melee && actual.ArcherUnits == archer;
        }

        internal struct SpawnMetadata
        {
            public string CaseLabel, TypeId, TypeLabel, Composition;
            public int TypeIndex, Count, ExpectedUnits, ActualUnits;
            public int MeleeUnits, ArcherUnits, Soldiers;
            public bool VerificationMatched;
            public int TotalInstances, SwarmPartition;
        }

        private struct SpawnVerification
        {
            public int UnitEntities, MeleeUnits, ArcherUnits, Soldiers;
        }
    }
}
