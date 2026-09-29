using Unity.Entities;

namespace Swarm
{
    /// <summary>FixedStep마다 틱을 1 증가.</summary>
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    internal partial struct SimTickSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (!SystemAPI.HasSingleton<SimTick>())
            {
                var e = state.EntityManager.CreateEntity(typeof(SimTick));
                state.EntityManager.SetComponentData(e, new SimTick { Value = 0 });
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            var tick = SystemAPI.GetSingletonRW<SimTick>();
            tick.ValueRW.Value++;
        }
    }
}
