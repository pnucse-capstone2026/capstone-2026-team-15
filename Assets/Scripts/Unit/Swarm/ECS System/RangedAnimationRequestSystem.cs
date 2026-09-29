using Unity.Burst;
using Unity.Entities;

namespace Swarm
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(RangedCombatStateSystem))]
    [UpdateBefore(typeof(UnitAnimationStateSystem))]
    [BurstCompile]
    internal partial struct RangedAnimationRequestSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency =
                new RangedAnimationRequestJob().ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct RangedAnimationRequestJob : IJobEntity
        {
            private const float BowShootDuration = 2.0f;

            private void Execute(
                ref UnitAnimationRequest request,
                in UnitAnimationProfile profile,
                in RangedAttackState rangedState,
                in RangedAttackTag rangedTag,
                in UnitTag unitTag)
            {
                if (!rangedState.ShootStarted)
                {
                    return;
                }

                request.HasRequest = true;
                request.Clip = profile.PrimaryAttackClip;
                request.Mode = UnitAnimationMode.OneShot;
                request.Duration = BowShootDuration;
                request.PlaybackSpeed = 1f / BowShootDuration;
                request.Priority = 8;
                request.ClearAfterApply = true;
            }
        }
    }
}
