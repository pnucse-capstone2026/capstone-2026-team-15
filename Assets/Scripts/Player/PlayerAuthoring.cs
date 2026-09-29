using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public class PlayerAuthoring : MonoBehaviour
{
    [Header("ECS Tags")]
    public bool enablePlayerTag = true;
    public bool enableBillboardTag = true;

    class Baker : Baker<PlayerAuthoring>
    {
        public override void Bake(PlayerAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic);

            AddComponent<PlayerTag>(entity); // 태그 추가
            SetComponentEnabled<PlayerTag>(entity, authoring.enablePlayerTag); // Inspector 토글에 따라 Enabled 상태만 on/off

            AddComponent<BillboardTag>(entity);
            SetComponentEnabled<BillboardTag>(entity, authoring.enableBillboardTag);

            // --- Equipment / Combat (테스트용 기본 장비) ---
            // 시작 무기: 검
            AddComponent(entity, new PlayerEquipment
            {
                CurrentWeapon = (byte)WeaponKind.Sword,
                SwordAttackCooldown = 0,
                SwordSkillCooldown = 0,
                BowAttackCooldown = 0,
                BowSkillCooldown = 0,
                DashCooldown = 0,
                LastMoveDir = new float3(0, 0, 1)
            });

            // 플레이어 액션(공격/스킬/대쉬 등) 이벤트 버퍼
            AddBuffer<PlayerActionEvent>(entity);
        }
    }
}
