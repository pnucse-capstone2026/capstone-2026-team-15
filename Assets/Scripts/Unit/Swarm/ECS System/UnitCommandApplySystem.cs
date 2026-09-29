using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace Swarm
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(SimTickSystem))]
    [BurstCompile]
    internal partial struct UnitCommandApplySystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<SimTick>();
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            uint nowTick = SystemAPI.GetSingleton<SimTick>().Value;

            foreach (var (target, swarmParams, animationRequest, cursor, cmdBuf) in SystemAPI
                         .Query<RefRW<UnitMoveTarget>, RefRW<SwarmParams>, RefRW<UnitAnimationRequest>, RefRW<UnitCommandCursor>, DynamicBuffer<UnitCommand>>()
                         .WithAll<UnitTag>())
            {
                uint originalLast = cursor.ValueRO.LastAppliedTick;
                uint maxApplied = originalLast;

                for (int i = 0; i < cmdBuf.Length; i++)
                {
                    var cmd = cmdBuf[i];
                    if (cmd.Tick <= originalLast || cmd.Tick > nowTick) continue;

                    switch (cmd.Type)
                    {
                        case UnitCommandType.MoveTo:
                            target.ValueRW.HasTarget = true;
                            target.ValueRW.Position = cmd.TargetPos;
                            break;

                        case UnitCommandType.SetSwarmParams:
                            swarmParams.ValueRW.SoldierCount = math.max(1, cmd.SoldierCount);
                            swarmParams.ValueRW.Columns = math.max(1, cmd.Columns);
                            swarmParams.ValueRW.SpacingX = cmd.SpacingX;
                            swarmParams.ValueRW.SpacingZ = cmd.SpacingZ;
                            swarmParams.ValueRW.BaseJitter = cmd.BaseJitter;
                            swarmParams.ValueRW.Seed = cmd.Seed;
                            break;

                        case UnitCommandType.SetAnimationState:
                            ApplyAnimationRequest(ref animationRequest.ValueRW, in cmd);
                            break;

                        case UnitCommandType.ClearAnimationState:
                            ClearAnimationRequest(ref animationRequest.ValueRW);
                            break;

                        case UnitCommandType.Fight:
                            ApplyFightRequest(ref animationRequest.ValueRW, in cmd);
                            break;
                    }
                    maxApplied = math.max(maxApplied, cmd.Tick);
                }
                cursor.ValueRW.LastAppliedTick = maxApplied;
            }
        }

        private static void ApplyAnimationRequest(ref UnitAnimationRequest request, in UnitCommand cmd)
        {
            request.HasRequest = true;
            request.Clip = cmd.AnimationClip;
            request.Mode = cmd.AnimationMode;
            request.Duration = math.max(0f, cmd.AnimationDuration);
            request.PlaybackSpeed = GetPlaybackSpeed(cmd.AnimationPlaybackSpeed);
            request.Priority = cmd.AnimationPriority;
            request.ClearAfterApply = cmd.AnimationClearAfterApply;
        }

        private static void ClearAnimationRequest(ref UnitAnimationRequest request)
        {
            request.HasRequest = false;
            request.Clip = UnitAnimationClipId.Idle;
            request.Mode = UnitAnimationMode.Loop;
            request.Duration = 0f;
            request.PlaybackSpeed = 1f;
            request.Priority = 0;
            request.ClearAfterApply = true;
        }

        private static void ApplyFightRequest(ref UnitAnimationRequest request, in UnitCommand cmd)
        {
            request.HasRequest = true;
            request.Clip = UnitAnimationClipId.Fight;
            request.Mode = UnitAnimationMode.OneShot;
            request.Duration = cmd.AnimationDuration > 0f ? cmd.AnimationDuration : 1.4f;
            request.PlaybackSpeed = GetPlaybackSpeed(cmd.AnimationPlaybackSpeed);
            request.Priority = cmd.AnimationPriority > 10 ? cmd.AnimationPriority : (byte)10;
            request.ClearAfterApply = true;
        }

        private static float GetPlaybackSpeed(float playbackSpeed)
        {
            return playbackSpeed > 0f ? playbackSpeed : 1f;
        }
    }
}
