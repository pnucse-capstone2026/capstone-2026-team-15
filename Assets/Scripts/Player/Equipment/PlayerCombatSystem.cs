using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.InputSystem;

/// <summary>
/// Q/E/R/Shift 입력을 받아 테스트용 전투/대쉬를 수행한다.
/// - Q: 현재 무기 기본공격 (쿨 1s)
/// - E: 현재 무기 기술 (쿨 5s)
/// - R: 무기 교체 (검<->활)
/// - Shift: 신발 대쉬 (쿨 3s, 전방으로 짧게 이동)
/// 
/// 데미지/판정은 고려하지 않고, 이벤트 버퍼에 액션 이벤트를 남겨 브리지(MonoBehaviour)가 간단한 이펙트를 띄운다.
/// </summary>

[UpdateInGroup(typeof(SimulationSystemGroup))]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct PlayerCombatSystem : ISystem
{
    private const float SwordAttackCD = 1f;
    private const float SwordSkillCD = 5f;
    private const float BowAttackCD = 1f;
    private const float BowSkillCD = 5f;
    private const float DashCD = 3f;

    private const float DashDuration = 0.15f;
    private const float DashDistance = 3.0f;

    public void OnUpdate(ref SystemState state)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        float dt = SystemAPI.Time.DeltaTime;

        // 입력(한 프레임 1회)
        bool doAttack = keyboard.qKey.wasPressedThisFrame;
        bool doSkill = keyboard.eKey.wasPressedThisFrame;
        bool doSwitch = keyboard.rKey.wasPressedThisFrame;
        bool doDash = keyboard.leftShiftKey.wasPressedThisFrame || keyboard.rightShiftKey.wasPressedThisFrame;

        // 구조 변경(대쉬 컴포넌트 추가/제거)을 위해 ECB 사용
        var ecb = new EntityCommandBuffer(Allocator.Temp);

        foreach (var (transform, equip, entity) in SystemAPI
                     .Query<RefRO<LocalTransform>, RefRW<PlayerEquipment>>()
                     .WithAll<PlayerTag>()
                     .WithEntityAccess())
        {
            // 0) 쿨타임 감소
            equip.ValueRW.SwordAttackCooldown = math.max(0, equip.ValueRO.SwordAttackCooldown - dt);
            equip.ValueRW.SwordSkillCooldown = math.max(0, equip.ValueRO.SwordSkillCooldown - dt);
            equip.ValueRW.BowAttackCooldown = math.max(0, equip.ValueRO.BowAttackCooldown - dt);
            equip.ValueRW.BowSkillCooldown = math.max(0, equip.ValueRO.BowSkillCooldown - dt);
            equip.ValueRW.DashCooldown = math.max(0, equip.ValueRO.DashCooldown - dt);

            DynamicBuffer<PlayerActionEvent> events = state.EntityManager.GetBuffer<PlayerActionEvent>(entity);

            // 1) 무기 교체
            if (doSwitch)
            {
                byte next = (byte)(((WeaponKind)equip.ValueRO.CurrentWeapon == WeaponKind.Sword) ? WeaponKind.Bow : WeaponKind.Sword);
                equip.ValueRW.CurrentWeapon = next;
                events.Add(new PlayerActionEvent
                {
                    ActionType = (byte)PlayerActionType.SwitchWeapon,
                    Weapon = next,
                    Position = transform.ValueRO.Position,
                    Direction = SafeDir(equip.ValueRO.LastMoveDir)
                });
            }

            // 2) 기본공격(Q)
            if (doAttack)
            {
                if ((WeaponKind)equip.ValueRO.CurrentWeapon == WeaponKind.Sword)
                {
                    if (equip.ValueRO.SwordAttackCooldown <= 0f)
                    {
                        equip.ValueRW.SwordAttackCooldown = SwordAttackCD;
                        events.Add(new PlayerActionEvent
                        {
                            ActionType = (byte)PlayerActionType.SwordAttack,
                            Weapon = (byte)WeaponKind.Sword,
                            Position = transform.ValueRO.Position,
                            Direction = SafeDir(equip.ValueRO.LastMoveDir)
                        });
                    }
                }
                else // Bow
                {
                    if (equip.ValueRO.BowAttackCooldown <= 0f)
                    {
                        equip.ValueRW.BowAttackCooldown = BowAttackCD;
                        events.Add(new PlayerActionEvent
                        {
                            ActionType = (byte)PlayerActionType.BowAttack,
                            Weapon = (byte)WeaponKind.Bow,
                            Position = transform.ValueRO.Position,
                            Direction = SafeDir(equip.ValueRO.LastMoveDir)
                        });
                    }
                }
            }

            // 3) 기술(E)
            if (doSkill)
            {
                if ((WeaponKind)equip.ValueRO.CurrentWeapon == WeaponKind.Sword)
                {
                    if (equip.ValueRO.SwordSkillCooldown <= 0f)
                    {
                        equip.ValueRW.SwordSkillCooldown = SwordSkillCD;
                        events.Add(new PlayerActionEvent
                        {
                            ActionType = (byte)PlayerActionType.SwordSkill,
                            Weapon = (byte)WeaponKind.Sword,
                            Position = transform.ValueRO.Position,
                            Direction = SafeDir(equip.ValueRO.LastMoveDir)
                        });
                    }
                }
                else // Bow
                {
                    if (equip.ValueRO.BowSkillCooldown <= 0f)
                    {
                        equip.ValueRW.BowSkillCooldown = BowSkillCD;
                        events.Add(new PlayerActionEvent
                        {
                            ActionType = (byte)PlayerActionType.BowSkill,
                            Weapon = (byte)WeaponKind.Bow,
                            Position = transform.ValueRO.Position,
                            Direction = SafeDir(equip.ValueRO.LastMoveDir)
                        });
                    }
                }
            }

            // 4) 대쉬(Shift)
            if (doDash && equip.ValueRO.DashCooldown <= 0f)
            {
                // 이미 대쉬 중이면 무시
                if (!state.EntityManager.HasComponent<PlayerDashState>(entity))
                {
                    equip.ValueRW.DashCooldown = DashCD;

                    float3 dir = SafeDir(equip.ValueRO.LastMoveDir);
                    float speed = DashDistance / DashDuration;

                    ecb.AddComponent(entity, new PlayerDashState
                    {
                        RemainingTime = DashDuration,
                        Speed = speed,
                        Direction = dir
                    });

                    events.Add(new PlayerActionEvent
                    {
                        ActionType = (byte)PlayerActionType.Dash,
                        Weapon = equip.ValueRO.CurrentWeapon,
                        Position = transform.ValueRO.Position,
                        Direction = dir
                    });
                }
            }
        }

        ecb.Playback(state.EntityManager);
        ecb.Dispose();
    }

    private static float3 SafeDir(float3 dir)
    {
        if (math.lengthsq(dir) < 0.0001f)
            return new float3(0, 0, 1);
        return math.normalize(new float3(dir.x, 0, dir.z));
    }
}
