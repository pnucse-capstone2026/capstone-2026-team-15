using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace Swarm
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [BurstCompile]
    internal partial struct SwarmInitSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            foreach (var (
                         spIter,
                         desyncIter,
                         rtIter,
                         slotsIter,
                         agentsIter,
                         varsIter,
                         combatStatesIter) in SystemAPI
                         .Query<
                             RefRO<SwarmParams>,
                             RefRO<SwarmAnimationDesyncTuning>,
                             RefRW<SwarmRuntime>,
                             DynamicBuffer<SoldierSlot>,
                             DynamicBuffer<SoldierAgent>,
                             DynamicBuffer<SoldierVariance>,
                             DynamicBuffer<SoldierCombatState>>()
                         .WithAll<UnitTag>())
            {
                var sp = spIter.ValueRO;
                var desync = desyncIter.ValueRO;

                int n = math.max(1, sp.SoldierCount);
                int cols = math.max(1, sp.Columns);

                float oneShotDelayMin = math.max(0f, desync.OneShotDelayMin);
                float oneShotDelayMax = math.max(oneShotDelayMin, desync.OneShotDelayMax);

                bool need =
                    rtIter.ValueRO.GeneratedCount != n ||
                    rtIter.ValueRO.GeneratedColumns != cols ||
                    rtIter.ValueRO.GeneratedSpacingX != sp.SpacingX ||
                    rtIter.ValueRO.GeneratedSpacingZ != sp.SpacingZ ||
                    rtIter.ValueRO.GeneratedSeed != sp.Seed ||
                    slotsIter.Length != n ||
                    agentsIter.Length != n ||
                    varsIter.Length != n ||
                    combatStatesIter.Length != n;

                if (!need)
                {
                    continue;
                }

                var slots = slotsIter;
                var agents = agentsIter;
                var vars = varsIter;
                var combatStates = combatStatesIter;

                if (slots.Length != n)
                {
                    slots.ResizeUninitialized(n);
                }

                if (agents.Length != n)
                {
                    agents.ResizeUninitialized(n);
                }

                if (vars.Length != n)
                {
                    vars.ResizeUninitialized(n);
                }

                if (combatStates.Length != n)
                {
                    combatStates.ResizeUninitialized(n);
                }

                var rng = Random.CreateFromIndex(sp.Seed);

                int rows = (int)math.ceil((float)n / cols);
                float centerRow = (rows - 1) / 2f;

                for (int i = 0; i < n; i++)
                {
                    int row = i / cols;
                    int col = i % cols;

                    int soldiersInRow = row == rows - 1
                        ? n - row * cols
                        : cols;

                    float rowCenterCol = (soldiersInRow - 1) / 2f;

                    float x = (col - rowCenterCol) * sp.SpacingX;
                    float z = -(row - centerRow) * sp.SpacingZ;

                    float frontness = rows > 1
                        ? 1f - (float)row / (rows - 1)
                        : 1f;

                    float lateral = cols > 1
                        ? ((float)col / (cols - 1)) * 2f - 1f
                        : 0f;

                    slots[i] = new SoldierSlot
                    {
                        BaseLocalPos = new float3(x, 0f, z),
                        Row = row,
                        Frontness = frontness,
                        RowNormalized = frontness,
                        LateralNormalized = lateral
                    };

                    vars[i] = new SoldierVariance
                    {
                        Seed = rng.NextUInt(),
                        MoveDelay = rng.NextFloat(0.8f, 1.2f),
                        SpeedMul = rng.NextFloat(0.9f, 1.1f),
                        Phase = rng.NextFloat(0f, 2f * math.PI),
                        AnimStartDelay = rng.NextFloat(oneShotDelayMin, oneShotDelayMax)
                    };

                    float2 jitter2D =
                        rng.NextFloat2Direction() *
                        rng.NextFloat(0f, sp.BaseJitter);

                    agents[i] = new SoldierAgent
                    {
                        CurrentLocalPos =
                            new float3(x, 0f, z) +
                            new float3(jitter2D.x, 0f, jitter2D.y),

                        Velocity = float3.zero,
                        AnimPhaseSpeed = 0f,
                        FacingYaw = 0f
                    };

                    combatStates[i] = new SoldierCombatState
                    {
                        InAttackRange = false,
                        TargetPos = float3.zero,
                        AttackTimer = 0f
                    };
                }

                rtIter.ValueRW.GeneratedCount = n;
                rtIter.ValueRW.GeneratedColumns = cols;
                rtIter.ValueRW.GeneratedSpacingX = sp.SpacingX;
                rtIter.ValueRW.GeneratedSpacingZ = sp.SpacingZ;
                rtIter.ValueRW.GeneratedSeed = sp.Seed;
            }
        }
    }
}