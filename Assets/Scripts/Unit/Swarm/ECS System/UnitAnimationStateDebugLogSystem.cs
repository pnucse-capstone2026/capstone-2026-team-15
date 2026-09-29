#if UNITY_EDITOR
using Unity.Entities;
using UnityEngine;

namespace Swarm
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(UnitAnimationStateSystem))]
    internal partial struct UnitAnimationStateDebugLogSystem : ISystem
    {
        private double _nextLogTime;

        public void OnCreate(ref SystemState state)
        {
            _nextLogTime = 0;
        }

        public void OnUpdate(ref SystemState state)
        {
            double elapsedTime = SystemAPI.Time.ElapsedTime;
            if (elapsedTime < _nextLogTime)
            {
                return;
            }

            _nextLogTime = elapsedTime + 1.0;

            foreach (var (animationState, request, fsm, locomotion, rangedState) in SystemAPI
                         .Query<RefRO<UnitAnimationState>, RefRO<UnitAnimationRequest>, RefRO<UnitFsm>, RefRO<UnitLocomotion>, RefRO<RangedAttackState>>()
                         .WithAll<UnitTag, RangedAttackTag>())
            {
                Debug.Log(
                    $"RangedAnimationState: Clip={animationState.ValueRO.Clip}, Mode={animationState.ValueRO.Mode}, " +
                    $"Time={animationState.ValueRO.NormalizedTime:0.00}, Request={request.ValueRO.HasRequest}, " +
                    $"ShootStarted={rangedState.ValueRO.ShootStarted}, FireTriggered={rangedState.ValueRO.FireTriggered}, " +
                    $"Released={rangedState.ValueRO.ProjectileReleased}, IsShooting={rangedState.ValueRO.IsShooting}, " +
                    $"ShootTimer={rangedState.ValueRO.ShootAnimationTimer:0.00}, Fsm={fsm.ValueRO.State}, " +
                    $"Locomotion={locomotion.ValueRO.State}");
                return;
            }

            foreach (var (animationState, request, fsm, locomotion) in SystemAPI
                         .Query<RefRO<UnitAnimationState>, RefRO<UnitAnimationRequest>, RefRO<UnitFsm>, RefRO<UnitLocomotion>>()
                         .WithAll<UnitTag>())
            {
                Debug.Log(
                    $"UnitAnimationState: Clip={animationState.ValueRO.Clip}, Mode={animationState.ValueRO.Mode}, " +
                    $"Time={animationState.ValueRO.NormalizedTime:0.00}, Request={request.ValueRO.HasRequest}, " +
                    $"Fsm={fsm.ValueRO.State}, Locomotion={locomotion.ValueRO.State}");
                return;
            }

            Debug.Log("UnitAnimationState: no UnitTag entity found in Default World.");
        }
    }
}
#endif
