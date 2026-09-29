using Unity.Entities;
using UnityEngine;

namespace Detection
{
    public class GlobalDetectionAuthoring : MonoBehaviour
    {
        public float cellSize = 10f;

        class Baker : Baker<GlobalDetectionAuthoring>
        {
            public override void Bake(GlobalDetectionAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.None);
                AddComponent(entity, new GlobalDetectionConfig { CellSize = authoring.cellSize });
            }
        }
    }
}