using Unity.Entities;
using UnityEngine;

public class CameraFollowTargetAuthoring : MonoBehaviour
{
    // 이 유닛을 카메라 타겟으로 만들지 여부 (인스펙터에서 체크)
    public bool isTarget = true;
}

public class CameraFollowTargetBaker : Baker<CameraFollowTargetAuthoring>
{
    public override void Bake(CameraFollowTargetAuthoring authoring)
    {
        if (!authoring.isTarget) return;

        var entity = GetEntity(TransformUsageFlags.Dynamic);
        AddComponent<CameraFollowTargetTag>(entity);
    }
}
