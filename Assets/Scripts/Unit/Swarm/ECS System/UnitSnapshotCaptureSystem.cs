using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Swarm
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(UnitSimulationSystem))]
    [BurstCompile]
    internal partial struct UnitSnapshotCaptureSystem : ISystem
    {
        private EntityQuery _query;

        private ComponentTypeHandle<SnapshotConfig> _cfgHandle;
        private ComponentTypeHandle<SnapshotRingState> _ringHandle;
        private ComponentTypeHandle<LocalTransform> _ltHandle;
        private ComponentTypeHandle<UnitVelocity> _velHandle;
        private ComponentTypeHandle<UnitFsm> _fsmHandle;
        private ComponentTypeHandle<UnitMoveTarget> _targetHandle;
        private ComponentTypeHandle<SwarmParams> _swarmHandle;
        private BufferTypeHandle<UnitSnapshot> _snapHandle;

        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimTick>();

            _query = state.GetEntityQuery(
                ComponentType.ReadOnly<UnitTag>(),
                ComponentType.ReadOnly<SnapshotConfig>(),
                ComponentType.ReadWrite<SnapshotRingState>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<UnitVelocity>(),
                ComponentType.ReadOnly<UnitFsm>(),
                ComponentType.ReadOnly<UnitMoveTarget>(),
                ComponentType.ReadOnly<SwarmParams>(),
                ComponentType.ReadWrite<UnitSnapshot>()
            );

            _cfgHandle = state.GetComponentTypeHandle<SnapshotConfig>(true);
            _ringHandle = state.GetComponentTypeHandle<SnapshotRingState>(false);
            _ltHandle = state.GetComponentTypeHandle<LocalTransform>(true);
            _velHandle = state.GetComponentTypeHandle<UnitVelocity>(true);
            _fsmHandle = state.GetComponentTypeHandle<UnitFsm>(true);
            _targetHandle = state.GetComponentTypeHandle<UnitMoveTarget>(true);
            _swarmHandle = state.GetComponentTypeHandle<SwarmParams>(true);
            _snapHandle = state.GetBufferTypeHandle<UnitSnapshot>(false);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            uint Tick = SystemAPI.GetSingleton<SimTick>().Value;

            _cfgHandle.Update(ref state);
            _ringHandle.Update(ref state);
            _ltHandle.Update(ref state);
            _velHandle.Update(ref state);
            _fsmHandle.Update(ref state);
            _targetHandle.Update(ref state);
            _swarmHandle.Update(ref state);
            _snapHandle.Update(ref state);

            var chunks = _query.ToArchetypeChunkArray(Allocator.TempJob);

            for (int chunkIndex = 0; chunkIndex < chunks.Length; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];

                var cfgs = chunk.GetNativeArray(ref _cfgHandle);
                var rings = chunk.GetNativeArray(ref _ringHandle);
                var lts = chunk.GetNativeArray(ref _ltHandle);
                var vels = chunk.GetNativeArray(ref _velHandle);
                var fsms = chunk.GetNativeArray(ref _fsmHandle);
                var targets = chunk.GetNativeArray(ref _targetHandle);
                var swarms = chunk.GetNativeArray(ref _swarmHandle);
                var snapsAccessor = chunk.GetBufferAccessor(ref _snapHandle);

                for (int i = 0; i < chunk.Count; i++)
                {
                    var cfg = cfgs[i];
                    if (cfg.Interval > 0 && Tick % cfg.Interval != 0) continue;

                    int cap = math.max(8, cfg.Capacity);
                    var ring = rings[i];
                    var snaps = snapsAccessor[i];

                    if (!ring.Initialized || snaps.Length != cap)
                    {
                        snaps.ResizeUninitialized(cap);
                        for (int s = 0; s < cap; s++) snaps[s] = default;
                        ring.Head = 0;
                        ring.Initialized = true;
                        ring.LatestTick = 0;
                    }

                    int head = ring.Head;
                    if ((uint)head >= (uint)snaps.Length) head = 0;

                    snaps[head] = new UnitSnapshot
                    {
                        Tick = Tick,
                        Pos = lts[i].Position,
                        Vel = vels[i].Value,
                        State = fsms[i].State,
                        HasTarget = targets[i].HasTarget,
                        TargetPos = targets[i].Position,
                        SoldierCount = swarms[i].SoldierCount,
                        Columns = swarms[i].Columns,
                        SpacingX = swarms[i].SpacingX,
                        SpacingZ = swarms[i].SpacingZ,
                        Seed = swarms[i].Seed
                    };

                    head = (head + 1) % snaps.Length;
                    ring.Head = head;
                    ring.LatestTick = Tick;
                    rings[i] = ring;
                }
            }

            chunks.Dispose();
        }
    }
}