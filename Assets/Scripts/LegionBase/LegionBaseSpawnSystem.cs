using Detection;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Swarm
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    internal partial struct LegionBaseSpawnSystem : ISystem
    {
        public void OnUpdate(ref SystemState state)
        {
            var ecb = new EntityCommandBuffer(Allocator.Temp);
            ComponentLookup<SwarmParams> swarmParamsLookup =
                SystemAPI.GetComponentLookup<SwarmParams>(true);

            foreach (var (
                         baseDataIter,
                         prefabIter,
                         overrideIter,
                         baseTransformIter,
                         unitBuffer,
                         baseEntity) in SystemAPI
                         .Query<
                             RefRW<LegionBaseData>,
                             RefRO<LegionUnitPrefab>,
                             RefRO<LegionBaseUnitOverrideData>,
                             RefRO<LocalTransform>,
                             DynamicBuffer<LegionBaseUnitElement>>()
                         .WithAll<LegionBaseTag>()
                         .WithEntityAccess())
            {
                var baseData = baseDataIter.ValueRO;
                var unitOverride = overrideIter.ValueRO;

                Entity unitPrefab = prefabIter.ValueRO.Prefab;
                LocalTransform baseTransform = baseTransformIter.ValueRO;

                if (!baseData.SpawnOnStart)
                {
                    continue;
                }

                if (baseData.Spawned)
                {
                    continue;
                }

                if (baseData.SpawnCount <= 0)
                {
                    baseDataIter.ValueRW.Spawned = true;
                    baseDataIter.ValueRW.SpawnedCount = 0;
                    baseDataIter.ValueRW.State = LegionBaseState.Deployed;
                    continue;
                }

                if (unitPrefab == Entity.Null)
                {
                    baseDataIter.ValueRW.State = LegionBaseState.Idle;
                    continue;
                }

                unitBuffer.Clear();

                int spawnCount = math.max(0, baseData.SpawnCount);
                int deployColumns = math.max(1, baseData.DeployColumns);
                float deploySpacing = math.max(0f, baseData.DeploySpacing);

                if (swarmParamsLookup.HasComponent(unitPrefab))
                {
                    SwarmParams swarmParams = swarmParamsLookup[unitPrefab];
                    deploySpacing = LegionSpawnPlacement.GetCenterSpacing(
                        in swarmParams,
                        deploySpacing,
                        baseTransform.Scale);
                }

                baseDataIter.ValueRW.State = LegionBaseState.Spawning;

                for (int i = 0; i < spawnCount; i++)
                {
                    Entity unitEntity = ecb.Instantiate(unitPrefab);

                    float3 gridOffset = GetGridOffset(
                        i,
                        deployColumns,
                        deploySpacing);

                    float3 localSpawnOffset = baseData.SpawnOffset + gridOffset;

                    float3 spawnPosition =
                        baseTransform.Position +
                        math.rotate(baseTransform.Rotation, localSpawnOffset);

                    float3 deployTargetPosition =
                        baseTransform.Position +
                        math.rotate(
                            baseTransform.Rotation,
                            baseData.DeployTargetPosition + gridOffset);

                    ecb.SetComponent(unitEntity, new LocalTransform
                    {
                        Position = spawnPosition,
                        Rotation = baseTransform.Rotation,
                        Scale = baseTransform.Scale
                    });

                    ecb.SetComponent(unitEntity, new UnitMoveTarget
                    {
                        HasTarget = true,
                        Position = deployTargetPosition
                    });

                    if (unitOverride.OverrideDetection)
                    {
                        ecb.SetComponent(unitEntity, new DetectionTag
                        {
                            FactionId = unitOverride.FactionId,
                            IsBase = unitOverride.IsBaseUnit,
                            SearchRange = unitOverride.SearchRange,
                            TauntWeight = unitOverride.TauntWeight,
                            TargetTauntThreshold = unitOverride.TargetTauntThreshold
                        });
                    }

                    if (unitOverride.OverrideAttack)
                    {
                        ecb.SetComponent(unitEntity, new UnitAttackParams
                        {
                            AttackRange = unitOverride.AttackRange,
                            AttackCooldown = unitOverride.AttackCooldown
                        });
                    }

                    ecb.AppendToBuffer(baseEntity, new LegionBaseUnitElement
                    {
                        UnitEntity = unitEntity,
                        Index = i,
                        DeployTargetPosition = deployTargetPosition
                    });
                }

                baseDataIter.ValueRW.Spawned = true;
                baseDataIter.ValueRW.SpawnedCount = spawnCount;
                baseDataIter.ValueRW.State = LegionBaseState.Deployed;
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// 여러 Unit을 생성하거나 배치할 때 겹치지 않도록
        /// 2D 격자 형태의 XZ 오프셋을 계산한다.
        /// </summary>
        private static float3 GetGridOffset(
            int index,
            int columns,
            float spacing)
        {
            columns = math.max(1, columns);

            int row = index / columns;
            int col = index % columns;

            float centerCol = (columns - 1) * 0.5f;

            float x = (col - centerCol) * spacing;
            float z = row * spacing;

            return new float3(x, 0f, z);
        }
    }
}
