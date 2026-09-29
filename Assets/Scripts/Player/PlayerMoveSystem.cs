using Unity.Entities;
using Unity.Transforms;
using Unity.Mathematics;
using UnityEngine.InputSystem;

// 이동 로직은 Simulation 그룹에서 실행
[UpdateInGroup(typeof(SimulationSystemGroup))] // 시뮬레이션 중 가장 먼저 실행)]
[UpdateBefore(typeof(TransformSystemGroup))]
public partial struct PlayerMoveSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        // 1. 입력 벡터 계산
        float2 moveInput = float2.zero;
        if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) moveInput.x += 1f;
        if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) moveInput.x -= 1f;
        if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) moveInput.y += 1f;
        if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) moveInput.y -= 1f;

        if (math.lengthsq(moveInput) <= 0) return;

        // 2. 정규화 및 속도 계산
        float3 moveDirection = new float3(moveInput.x, 0, moveInput.y);
        float3 velocity = math.normalize(moveDirection) * 5f * SystemAPI.Time.DeltaTime;

        // 대쉬 중에는 일반 이동을 잠시 막는다.
        foreach (var (transform, equip) in SystemAPI
                     .Query<RefRW<LocalTransform>, RefRW<PlayerEquipment>>()
                     .WithAll<PlayerTag>()
                     .WithNone<PlayerDashState>())
        {
            transform.ValueRW.Position += velocity;
            equip.ValueRW.LastMoveDir = math.normalize(moveDirection);
        }
    }
}
