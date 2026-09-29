using Detection;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Swarm
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(UnitSimulationSystem))]
    [UpdateBefore(typeof(UnitAnimationStateSystem))]
    [BurstCompile]
    internal partial struct RangedCombatStateSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var job = new RangedCombatStateJob
            {
                Dt = SystemAPI.Time.DeltaTime
            };

            state.Dependency =
                job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct RangedCombatStateJob : IJobEntity
        {
            private const float ShootAnimationDuration = 2.0f;

            public float Dt;

            private void Execute(
                ref RangedAttackState state,
                in RangedAttackParams rangedParams,
                in DetectionTarget detectionTarget,
                in LocalTransform transform,
                in UnitTag unitTag,
                in RangedAttackTag rangedTag)
            {
                state.ShootStarted = false;
                state.FireTriggered = false;

                float previousShootTimer =
                    state.ShootAnimationTimer;

                state.ShootAnimationTimer =
                    math.max(
                        0f,
                        state.ShootAnimationTimer - Dt);
                state.IsShooting = state.ShootAnimationTimer > 0f;

                UpdateProjectileRelease(
                    ref state,
                    in rangedParams,
                    previousShootTimer);

                bool canShoot =
                    detectionTarget.HasTarget;

                if (!detectionTarget.HasTarget)
                {
                    canShoot = false;
                }
                else
                {
                    GetRangeBounds(
                        in rangedParams,
                        out float minRange,
                        out float maxRange);

                    if (maxRange <= 0.0001f)
                    {
                        canShoot = false;
                    }
                    else
                    {
                        float3 toTarget =
                            detectionTarget.TargetPos -
                            transform.Position;

                        toTarget.y = 0f;

                        float distanceSq =
                            math.lengthsq(toTarget);

                        canShoot =
                            distanceSq >= minRange * minRange &&
                            distanceSq <= maxRange * maxRange;
                    }
                }

                if (!canShoot)
                {
                    state.Cooldown =
                        math.max(
                            0f,
                            state.Cooldown - Dt);
                    state.VolleyTimer = 0f;
                    return;
                }

                UpdateReadyState(
                    ref state,
                    in rangedParams,
                    Dt);
            }

            private static void UpdateReadyState(
                ref RangedAttackState state,
                in RangedAttackParams rangedParams,
                float dt)
            {
                state.Cooldown =
                    math.max(
                        0f,
                        state.Cooldown - dt);

                state.VolleyTimer +=
                    math.max(
                        0f,
                        dt);

                if (state.Cooldown > 0f)
                {
                    return;
                }

                state.IsShooting = true;
                state.ShootStarted = true;
                state.ShootAnimationTimer = ShootAnimationDuration;
                state.ProjectileReleased = false;
                state.Cooldown =
                    math.max(
                        0f,
                        rangedParams.VolleyInterval);
                state.VolleyTimer = 0f;
            }

            private static void UpdateProjectileRelease(
                ref RangedAttackState state,
                in RangedAttackParams rangedParams,
                float previousShootTimer)
            {
                if (state.ProjectileReleased ||
                    previousShootTimer <= 0f)
                {
                    return;
                }

                float previousNormalized =
                    1f -
                    math.saturate(
                        previousShootTimer /
                        ShootAnimationDuration);

                float currentNormalized =
                    1f -
                    math.saturate(
                        state.ShootAnimationTimer /
                        ShootAnimationDuration);

                float releaseNormalized =
                    math.saturate(
                        rangedParams.ShootTimingNormalized);

                if (previousNormalized < releaseNormalized &&
                    currentNormalized >= releaseNormalized)
                {
                    state.FireTriggered = true;
                    state.ProjectileReleased = true;
                }
            }

            private static void GetRangeBounds(
                in RangedAttackParams rangedParams,
                out float minRange,
                out float maxRange)
            {
                minRange =
                    math.max(
                        0f,
                        rangedParams.MinRange);

                maxRange =
                    math.max(
                        minRange,
                        rangedParams.MaxRange);

                if (maxRange <= 0.0001f)
                {
                    maxRange =
                        math.max(
                            minRange,
                            rangedParams.PreferredRange);
                }
            }
        }
    }
}
