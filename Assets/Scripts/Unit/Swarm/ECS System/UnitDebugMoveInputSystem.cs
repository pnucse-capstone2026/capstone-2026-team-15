using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Swarm
{
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    internal partial struct UnitDebugMoveInputSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<UnitTag>();
            state.RequireForUpdate<UnitMoveTarget>();
        }

        public void OnUpdate(ref SystemState state)
        {
            var mouse = Mouse.current;
            var keyboard = Keyboard.current;
            if (mouse == null && keyboard == null) return;

#if UNITY_EDITOR
            if (keyboard != null)
            {
                if (keyboard.fKey.wasPressedThisFrame)
                {
                    EnqueueAnimationCommand(ref state, UnitCommandType.Fight, UnitAnimationClipId.Fight, UnitAnimationMode.OneShot, 0f, 10, true);
                    return;
                }

                if (keyboard.gKey.wasPressedThisFrame)
                {
                    EnqueueAnimationCommand(ref state, UnitCommandType.SetAnimationState, UnitAnimationClipId.Fight, UnitAnimationMode.Loop, 0f, 10, false);
                    return;
                }

                if (keyboard.hKey.wasPressedThisFrame)
                {
                    EnqueueAnimationCommand(ref state, UnitCommandType.ClearAnimationState, UnitAnimationClipId.Idle, UnitAnimationMode.Loop, 0f, 0, true);
                    return;
                }
            }
#endif

            if (mouse == null) return;

            bool issueMove = mouse.leftButton.wasPressedThisFrame;
            bool clearMove = mouse.rightButton.wasPressedThisFrame;

            if (!issueMove && !clearMove) return;

            if (clearMove)
            {
                foreach (var target in SystemAPI.Query<RefRW<UnitMoveTarget>>().WithAll<UnitTag>())
                {
                    target.ValueRW.HasTarget = false;
                }
                return;
            }

            var cam = Camera.main;
            if (cam == null) return;

            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            var ground = new Plane(Vector3.up, Vector3.zero);

            if (!ground.Raycast(ray, out float enter)) return;

            Vector3 hitPoint = ray.GetPoint(enter);
            float3 targetPos = new float3(hitPoint.x, 0f, hitPoint.z);

            foreach (var target in SystemAPI.Query<RefRW<UnitMoveTarget>>().WithAll<UnitTag>())
            {
                target.ValueRW.HasTarget = true;
                target.ValueRW.Position = targetPos;
            }
        }

#if UNITY_EDITOR
        private void EnqueueAnimationCommand(
            ref SystemState state,
            UnitCommandType type,
            UnitAnimationClipId clip,
            UnitAnimationMode mode,
            float duration,
            byte priority,
            bool clearAfterApply)
        {
            uint tick = SystemAPI.HasSingleton<SimTick>() ? SystemAPI.GetSingleton<SimTick>().Value + 1 : 1;

            foreach (var commands in SystemAPI.Query<DynamicBuffer<UnitCommand>>().WithAll<UnitTag>())
            {
                commands.Add(new UnitCommand
                {
                    Tick = tick,
                    Type = type,
                    AnimationClip = clip,
                    AnimationMode = mode,
                    AnimationDuration = duration,
                    AnimationPlaybackSpeed = 1f,
                    AnimationPriority = priority,
                    AnimationClearAfterApply = clearAfterApply
                });
            }
        }
#endif
    }
}
