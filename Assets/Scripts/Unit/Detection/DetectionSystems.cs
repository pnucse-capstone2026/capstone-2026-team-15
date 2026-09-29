using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Detection
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [BurstCompile]
    public partial struct GlobalStrategySystem : ISystem
    {
        public struct GridNode
        {
            public Entity Entity;
            public float3 Position;
            public int FactionId;
            public float TauntWeight;
            public bool IsObstacle; // 장애물 여부
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<GlobalDetectionConfig>(out var config)) return;

            // 1. 유닛 및 장애물 쿼리 빌드
            var unitQuery = SystemAPI.QueryBuilder()
                .WithAll<DetectionTag, LocalToWorld>()
                .Build();
            var obstacleQuery = SystemAPI.QueryBuilder().WithAll<ObstacleTag, LocalToWorld>().Build();

            int totalCount = unitQuery.CalculateEntityCount() + obstacleQuery.CalculateEntityCount();
            if (totalCount == 0) return;

            var grid = new NativeParallelMultiHashMap<int, GridNode>(totalCount, Allocator.TempJob);
            var writer = grid.AsParallelWriter();

            // 2. 그리드 업데이트 (유닛 + 장애물)
            float cellSize =
                math.max(
                    0.001f,
                    config.CellSize);

            state.Dependency = new GridUpdateJob { Grid = writer, CellSize = cellSize }.ScheduleParallel(unitQuery, state.Dependency);
            state.Dependency = new ObstacleGridUpdateJob { Grid = writer, CellSize = cellSize }.ScheduleParallel(obstacleQuery, state.Dependency);

            // 3. 탐색 실행 (베이스와 유닛 분리)
            // 베이스: 전략적 탐색 (가장 가까운 적)
            state.Dependency = new BaseDiscoveryJob { Grid = grid, CellSize = cellSize }.ScheduleParallel(state.Dependency);
            // 유닛: 전술적 탐색 (도발 수치 및 차폐 고려)
            state.Dependency = new UnitDiscoveryJob { Grid = grid, CellSize = cellSize, PenaltyWeight = config.ObstaclePenaltyWeight }.ScheduleParallel(state.Dependency);

            state.Dependency = grid.Dispose(state.Dependency);
        }

        [BurstCompile]
        partial struct GridUpdateJob : IJobEntity
        {
            public NativeParallelMultiHashMap<int, GridNode>.ParallelWriter Grid;
            public float CellSize;
            void Execute(Entity entity, in DetectionTag tag, in LocalToWorld ltw)
            {
                float3 pos = ltw.Position;
                int hash = (int)math.hash(new int2((int)math.floor(pos.x / CellSize), (int)math.floor(pos.z / CellSize)));
                Grid.Add(hash, new GridNode { Entity = entity, Position = pos, FactionId = tag.FactionId, TauntWeight = tag.TauntWeight, IsObstacle = false });
            }
        }

        [BurstCompile]
        partial struct ObstacleGridUpdateJob : IJobEntity
        {
            public NativeParallelMultiHashMap<int, GridNode>.ParallelWriter Grid;
            public float CellSize;
            void Execute(in ObstacleTag obstacle, in LocalToWorld ltw)
            {
                float3 pos = ltw.Position;
                int hash = (int)math.hash(new int2((int)math.floor(pos.x / CellSize), (int)math.floor(pos.z / CellSize)));
                Grid.Add(hash, new GridNode { IsObstacle = true, Position = pos, TauntWeight = obstacle.Penalty });
            }
        }

        [BurstCompile]
        partial struct BaseDiscoveryJob : IJobEntity
        {
            [ReadOnly] public NativeParallelMultiHashMap<int, GridNode> Grid;
            public float CellSize;
            void Execute(ref DetectionTarget target, in DetectionTag tag, in LocalToWorld ltw)
            {
                if (!tag.IsBase) return;
                // 베이스는 기존과 동일하게 가장 가까운 적을 찾음 (전략적 방향 제시)
                float3 myPos = ltw.Position;
                float minSq = tag.SearchRange * tag.SearchRange;
                bool found = false;
                int2 cell = GetGridCell(myPos, CellSize);
                int cellRadius = GetSearchCellRadius(tag.SearchRange, CellSize);

                for (int x = -cellRadius; x <= cellRadius; x++)
                {
                    for (int z = -cellRadius; z <= cellRadius; z++)
                    {
                        int hash = (int)math.hash(cell + new int2(x, z));
                        foreach (var node in Grid.GetValuesForKey(hash))
                        {
                            if (node.IsObstacle || node.FactionId == tag.FactionId) continue;
                            float d = math.distancesq(myPos, node.Position);
                            if (d < minSq) { minSq = d; target.CenterTarget = node.Entity; target.TargetPos = node.Position; found = true; }
                        }
                    }
                }
                target.HasTarget = found;
            }
        }

        [BurstCompile]
        partial struct UnitDiscoveryJob : IJobEntity
        {
            [ReadOnly] public NativeParallelMultiHashMap<int, GridNode> Grid;
            public float CellSize;
            public float PenaltyWeight;

            void Execute(ref DetectionTarget target, in DetectionTag tag, in LocalToWorld ltw)
            {
                if (tag.IsBase) return; // 유닛 전용 로직

                float3 myPos = ltw.Position;
                float searchRangeSq = tag.SearchRange * tag.SearchRange;

                Entity bestTarget = Entity.Null;
                float3 bestPos = float3.zero;
                float minScore = float.MaxValue;
                bool priorityFound = false;

                int2 cell = GetGridCell(myPos, CellSize);
                int cellRadius = GetSearchCellRadius(tag.SearchRange, CellSize);

                for (int x = -cellRadius; x <= cellRadius; x++)
                {
                    for (int z = -cellRadius; z <= cellRadius; z++)
                    {
                        int hash = (int)math.hash(cell + new int2(x, z));

                        // 1차 루프: 해당 셀의 장애물 페널티 확인 (단순화를 위해 셀 내 장애물 존재 시 페널티 부여)
                        float cellPenalty = 1.0f;
                        foreach (var node in Grid.GetValuesForKey(hash))
                        {
                            if (node.IsObstacle) cellPenalty += node.TauntWeight;
                        }

                        // 2차 루프: 타겟 탐색
                        foreach (var node in Grid.GetValuesForKey(hash))
                        {
                            if (node.IsObstacle || node.FactionId == tag.FactionId) continue;

                            float dSq = math.distancesq(myPos, node.Position);
                            if (dSq > searchRangeSq) continue;

                            // 차폐 구현: 장애물 페널티를 거리 가중치로 환산
                            float effectiveScore = dSq * cellPenalty;
                            bool isPriority = node.TauntWeight >= tag.TargetTauntThreshold;

                            // 타겟 선정 로직: 도발 수치가 높은 적이 우선, 동일 조건 시 유효 거리가 가까운 적 선택
                            if (bestTarget == Entity.Null || (isPriority && !priorityFound) || (isPriority == priorityFound && effectiveScore < minScore))
                            {
                                bestTarget = node.Entity;
                                bestPos = node.Position;
                                minScore = effectiveScore;
                                priorityFound = isPriority;
                            }
                        }
                    }
                }
                target.CenterTarget = bestTarget;
                target.TargetPos = bestPos;
                target.HasTarget = bestTarget != Entity.Null;
            }
        }

        private static int2 GetGridCell(float3 position, float cellSize)
        {
            float safeCellSize =
                math.max(
                    0.001f,
                    cellSize);

            return new int2(
                (int)math.floor(position.x / safeCellSize),
                (int)math.floor(position.z / safeCellSize));
        }

        private static int GetSearchCellRadius(float searchRange, float cellSize)
        {
            float safeCellSize =
                math.max(
                    0.001f,
                    cellSize);

            return math.max(
                1,
                (int)math.ceil(math.max(0f, searchRange) / safeCellSize));
        }

        // Burst를 끄고 실행하는 디버그 전용 시스템
        [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
        [UpdateAfter(typeof(GlobalStrategySystem))]
        public partial struct DetectionDebugSystem : ISystem
        {
            public void OnUpdate(ref SystemState state)
            {
                // DetectionTarget이 있는 모든 유닛/베이스를 순회
                foreach (var (target, ltw, tag) in SystemAPI.Query<RefRO<DetectionTarget>, RefRO<LocalToWorld>, RefRO<DetectionTag>>())
                {
                    if (target.ValueRO.HasTarget)
                    {
                        // 베이스는 파란색, 유닛은 빨간색으로 구분하여 선을 그림
                        Color lineColor = tag.ValueRO.IsBase ? Color.blue : Color.red;

                        // 시작점: 내 위치, 끝점: 타겟 위치 (식별을 위해 Y축 +1)
                        Vector3 start = ltw.ValueRO.Position + new Unity.Mathematics.float3(0, 1, 0);
                        Vector3 end = target.ValueRO.TargetPos + new Unity.Mathematics.float3(0, 1, 0);

                        Debug.DrawLine(start, end, lineColor);
                    }
                }
            }
        }
    }
}
