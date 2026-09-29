using Unity.Entities;
using UnityEngine;

public struct PlayerVisualLink : IComponentData
{
    public Entity VisualRootEntity;
    public float TurnSpeed;
}

public class PlayerVisualAuthoring : MonoBehaviour
{
    [Min(0f)]
    public float turnSpeed = 12f;

    public Transform visualRoot;

    class Baker : Baker<PlayerVisualAuthoring>
    {
        public override void Bake(PlayerVisualAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);
            var visualRoot = authoring.visualRoot != null
                ? GetEntity(authoring.visualRoot.gameObject, TransformUsageFlags.Dynamic)
                : entity;

            AddComponent(entity, new PlayerVisualLink
            {
                VisualRootEntity = visualRoot,
                TurnSpeed = authoring.turnSpeed
            });
        }
    }
}
