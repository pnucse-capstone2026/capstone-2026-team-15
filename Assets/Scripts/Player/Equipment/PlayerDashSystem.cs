using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

/// <summary>
/// PlayerDashState가 있는 동안 플레이어를 전방으로 빠르게 이동시킨다.
/// </summary>
[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(PlayerMoveSystem))]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct PlayerDashSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        float dt = SystemAPI.Time.DeltaTime;
        if (dt <= 0f) return;

        // 구조 변경(컴포넌트 제거)을 위해 ECB 사용
        var ecb = new Unity.Entities.EntityCommandBuffer(Unity.Collections.Allocator.Temp);

        foreach (var (transform, dash, entity) in SystemAPI
                     .Query<RefRW<LocalTransform>, RefRW<PlayerDashState>>()
                     .WithAll<PlayerTag>()
                     .WithEntityAccess())
        {
            dash.ValueRW.RemainingTime -= dt;
            float3 delta = dash.ValueRO.Direction * dash.ValueRO.Speed * dt;
            transform.ValueRW.Position += delta;

            if (dash.ValueRO.RemainingTime <= 0f)
                ecb.RemoveComponent<PlayerDashState>(entity);
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }
}
