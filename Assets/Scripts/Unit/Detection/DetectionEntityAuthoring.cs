using Unity.Entities;
using UnityEngine;

namespace Detection
{
    public class DetectionEntityAuthoring : MonoBehaviour
    {
        public int factionId;
        public bool isBase;
        public float searchRange = 20f;

        [Header("Tactical Params")]
        public float tauntWeight = 1f;            // 나의 도발치
        public float targetTauntThreshold = 5f;  // 우선 공격할 적의 최소 도발치

        class Baker : Baker<DetectionEntityAuthoring>
        {
            public override void Bake(DetectionEntityAuthoring authoring)
            {
                var entity = GetEntity(TransformUsageFlags.Dynamic);

                AddComponent(entity, new DetectionTag
                {
                    FactionId = authoring.factionId,
                    IsBase = authoring.isBase,
                    SearchRange = authoring.searchRange,
                    TauntWeight = authoring.tauntWeight,
                    TargetTauntThreshold = authoring.targetTauntThreshold
                });

                AddComponent(entity, new DetectionTarget());
            }
        }
    }
}