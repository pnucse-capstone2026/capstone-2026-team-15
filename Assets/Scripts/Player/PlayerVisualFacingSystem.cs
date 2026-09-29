using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateAfter(typeof(PlayerMoveSystem))]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct PlayerVisualFacingSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        float dt = SystemAPI.Time.DeltaTime;
        var entityManager = state.EntityManager;

        foreach (var (visualLink, equipment) in SystemAPI
                     .Query<RefRO<PlayerVisualLink>, RefRO<PlayerEquipment>>()
                     .WithAll<PlayerTag>())
        {
            var visualEntity = visualLink.ValueRO.VisualRootEntity;
            if (visualEntity == Entity.Null ||
                !entityManager.Exists(visualEntity) ||
                !entityManager.HasComponent<LocalTransform>(visualEntity))
            {
                continue;
            }

            float3 lastMoveDir = equipment.ValueRO.LastMoveDir;
            float2 planarDir = new float2(lastMoveDir.x, lastMoveDir.z);
            if (math.lengthsq(planarDir) < 0.0001f)
            {
                continue;
            }

            float3 facingDir = math.normalize(new float3(planarDir.x, 0f, planarDir.y));
            quaternion targetRotation = quaternion.LookRotationSafe(facingDir, math.up());

            LocalTransform visualTransform = entityManager.GetComponentData<LocalTransform>(visualEntity);
            float turnLerp = visualLink.ValueRO.TurnSpeed <= 0f
                ? 1f
                : 1f - math.exp(-visualLink.ValueRO.TurnSpeed * dt);

            visualTransform.Rotation = math.slerp(visualTransform.Rotation, targetRotation, turnLerp);
            entityManager.SetComponentData(visualEntity, visualTransform);
        }
    }
}
