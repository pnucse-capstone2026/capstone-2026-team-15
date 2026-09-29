// ObstacleAuthoring.cs
using Detection;
using Unity.Entities;
using UnityEngine;

namespace Detection
{
    public class ObstacleAuthoring : MonoBehaviour
    {
        public float penalty = 5.0f; // 장애물이 탐색 점수에 주는 가중치

        class Baker : Baker<ObstacleAuthoring>
        {
            public override void Bake(ObstacleAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent(entity, new ObstacleTag { Penalty = authoring.penalty });
            }
        }
    }
}