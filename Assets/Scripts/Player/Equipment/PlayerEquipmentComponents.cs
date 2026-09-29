using Unity.Entities;
using Unity.Mathematics;

/// <summary>
/// 테스트용 장비/전투 컴포넌트 모음.
/// - 무기 2종(검/활) + 신발(대쉬)
/// - UI/이펙트는 별도의 브리지(MonoBehaviour)에서 참조한다.
/// </summary>

public enum WeaponKind : byte
{
    Sword = 0,
    Bow = 1,
}

public enum PlayerActionType : byte
{
    SwordAttack = 0,
    SwordSkill = 1,
    BowAttack = 2,
    BowSkill = 3,
    Dash = 4,
    SwitchWeapon = 5,
}

public struct PlayerEquipment : IComponentData
{
    public byte CurrentWeapon; // WeaponKind

    // 남은 쿨타임(초)
    public float SwordAttackCooldown;
    public float SwordSkillCooldown;
    public float BowAttackCooldown;
    public float BowSkillCooldown;
    public float DashCooldown;

    // 마지막 이동 방향(공격/대쉬 방향)
    public float3 LastMoveDir;
}

/// <summary>
/// 대쉬 진행 중 상태
/// </summary>
public struct PlayerDashState : IComponentData
{
    public float RemainingTime;
    public float Speed;
    public float3 Direction;
}

/// <summary>
/// 전투/대쉬 발생 이벤트. (VFX/사운드/디버그용)
/// </summary>
public struct PlayerActionEvent : IBufferElementData
{
    public byte ActionType;   // PlayerActionType
    public byte Weapon;       // WeaponKind
    public float3 Position;
    public float3 Direction;
}
