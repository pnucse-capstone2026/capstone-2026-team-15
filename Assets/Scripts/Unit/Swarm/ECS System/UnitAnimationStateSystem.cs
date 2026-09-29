using Detection;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Swarm
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(UnitCommandApplySystem))]
    [UpdateAfter(typeof(UnitSimulationSystem))]
    [UpdateAfter(typeof(CrowdSimulationSystem))]
    [UpdateAfter(typeof(RangedAnimationRequestSystem))]
    [BurstCompile]
    internal partial struct UnitAnimationStateSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var job = new UnitAnimationStateJob
            {
                Dt = SystemAPI.Time.DeltaTime,
                TargetTransformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true),
                TargetSwarmParamsLookup = SystemAPI.GetComponentLookup<SwarmParams>(true)
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct UnitAnimationStateJob : IJobEntity
        {
            public float Dt;

            [ReadOnly] public ComponentLookup<LocalTransform> TargetTransformLookup;
            [ReadOnly] public ComponentLookup<SwarmParams> TargetSwarmParamsLookup;

            private void Execute(
                Entity entity,
                ref UnitAnimationState animationState,
                ref UnitAnimationRequest request,
                in UnitFsm fsm,
                in UnitLocomotion locomotion,
                in LocalTransform lt,
                in UnitAttackParams attackParams,
                in DetectionTarget detectionTarget,
                in DynamicBuffer<SoldierAgent> agents,
                DynamicBuffer<SoldierCombatState> combatStates,
                in UnitTag tag)
            {
                // ==================================================
                // [1] Unit 단위 애니메이션 FSM 갱신
                // ==================================================

                bool lockActive = animationState.LockRemainingTime > 0f;

                if (lockActive)
                {
                    animationState.LockRemainingTime = math.max(
                        0f,
                        animationState.LockRemainingTime - Dt);

                    lockActive = animationState.LockRemainingTime > 0f;
                }

                bool requestApplied = false;

                if (request.HasRequest && (!lockActive || request.Priority >= animationState.Priority))
                {
                    ApplyClip(
                        ref animationState,
                        request.Clip,
                        request.Mode,
                        request.PlaybackSpeed,
                        request.Duration,
                        request.Priority);

                    requestApplied = true;

                    if (request.ClearAfterApply)
                    {
                        ClearRequest(ref request);
                    }
                }

                if (!requestApplied && !lockActive)
                {
                    UnitAnimationClipId fallbackClip = GetFallbackClip(
                        fsm.State,
                        locomotion.State);

                    ApplyClip(
                        ref animationState,
                        fallbackClip,
                        UnitAnimationMode.Loop,
                        1f,
                        0f,
                        0);
                }

                AdvanceTime(
                    ref animationState,
                    Dt);

                // ==================================================
                // [2] Soldier 인스턴스 단위 Fight 판정
                // ==================================================
                //
                // 기존:
                // - 병사 위치와 DetectionTarget.TargetPos 사이의 거리로 판정.
                // - 상대 유닛이 넓은 대형이어도 중심점 근처 병사만 Fight가 됨.
                //
                // 수정:
                // - 병사 위치에서 상대 Unit Footprint의 가장 가까운 경계점까지의 거리로 판정.
                // - 상대 전열과 맞닿은 병사들이 자연스럽게 Fight 상태가 됨.

                UpdateSoldierCombatStates(
                    entity,
                    in lt,
                    in attackParams,
                    in detectionTarget,
                    in agents,
                    combatStates,
                    Dt);
            }

            /// <summary>
            /// FSM과 Locomotion에 따른 유닛 대표 애니메이션 클립 선택.
            /// 병사별 Fight를 별도로 처리하기 위해 Engaging만으로 전체 Fight를 강제하지 않는다.
            /// </summary>
            private static UnitAnimationClipId GetFallbackClip(
                UnitState unitState,
                LocomotionState locomotionState)
            {
                if (locomotionState == LocomotionState.Walk)
                {
                    return UnitAnimationClipId.Walk;
                }

                if (locomotionState == LocomotionState.Run)
                {
                    return UnitAnimationClipId.Run;
                }

                return UnitAnimationClipId.Idle;
            }

            /// <summary>
            /// 지정된 클립을 UnitAnimationState에 반영.
            /// 클립이 바뀌면 NormalizedTime을 초기화한다.
            /// </summary>
            private static void ApplyClip(
                ref UnitAnimationState state,
                UnitAnimationClipId clip,
                UnitAnimationMode mode,
                float playbackSpeed,
                float duration,
                byte priority)
            {
                if (state.Clip != clip)
                {
                    state.PreviousClip = state.Clip;
                    state.NormalizedTime = 0f;
                    state.BlendTime = 0f;
                }

                state.Clip = clip;
                state.Mode = mode;
                state.PlaybackSpeed = playbackSpeed > 0f ? playbackSpeed : 1f;
                state.LockRemainingTime = math.max(0f, duration);
                state.Priority = priority;
            }

            /// <summary>
            /// 애니메이션 시간을 진행한다.
            /// OneShot은 0~1 사이로 고정하고, Loop는 frac으로 반복한다.
            /// </summary>
            private static void AdvanceTime(
                ref UnitAnimationState state,
                float dt)
            {
                state.NormalizedTime += dt * state.PlaybackSpeed;

                if (state.Mode == UnitAnimationMode.OneShot)
                {
                    state.NormalizedTime = math.saturate(state.NormalizedTime);
                    return;
                }

                state.NormalizedTime = math.frac(state.NormalizedTime);
            }

            /// <summary>
            /// 애니메이션 요청 초기화.
            /// </summary>
            private static void ClearRequest(
                ref UnitAnimationRequest request)
            {
                request.HasRequest = false;
                request.Clip = UnitAnimationClipId.Idle;
                request.Mode = UnitAnimationMode.Loop;
                request.Duration = 0f;
                request.PlaybackSpeed = 1f;
                request.Priority = 0;
                request.ClearAfterApply = true;
            }

            // ======================================================
            //  Soldier Combat State
            // ======================================================

            /// <summary>
            /// SoldierAgent별 공격 사거리 판정.
            ///
            /// 기존처럼 DetectionTarget.TargetPos 하나를 기준으로 하지 않고,
            /// 상대 Unit의 Swarm Footprint 경계점까지의 거리로 판정한다.
            /// </summary>
            private void UpdateSoldierCombatStates(
                Entity selfEntity,
                in LocalTransform lt,
                in UnitAttackParams attackParams,
                in DetectionTarget detectionTarget,
                in DynamicBuffer<SoldierAgent> agents,
                DynamicBuffer<SoldierCombatState> combatStates,
                float dt)
            {
                int count = math.min(
                    agents.Length,
                    combatStates.Length);

                if (count <= 0)
                {
                    return;
                }

                if (!detectionTarget.HasTarget || attackParams.AttackRange <= 0f)
                {
                    ClearCombatStates(
                        combatStates,
                        count);

                    return;
                }

                bool hasTargetFootprint = TryGetTargetFootprint(
                    selfEntity,
                    in detectionTarget,
                    out float3 targetPosition,
                    out quaternion targetRotation,
                    out SwarmParams targetSwarmParams);

                float attackRange =
                    math.max(0f, attackParams.AttackRange);

                float attackRangeSq =
                    attackRange *
                    attackRange;

                for (int i = 0; i < count; i++)
                {
                    SoldierAgent agent = agents[i];
                    SoldierCombatState combat = combatStates[i];

                    float3 soldierWorldPos =
                        lt.Position +
                        agent.CurrentLocalPos;

                    float3 targetPoint;

                    if (hasTargetFootprint)
                    {
                        targetPoint =
                            GetClosestPointOnTargetFootprintBoundary(
                                soldierWorldPos,
                                targetPosition,
                                targetRotation,
                                in targetSwarmParams,
                                0f);
                    }
                    else
                    {
                        // fallback:
                        // 타겟 Entity를 읽을 수 없는 경우에는 기존 TargetPos 기준으로 판정.
                        targetPoint = detectionTarget.TargetPos;
                    }

                    float3 toTarget =
                        targetPoint -
                        soldierWorldPos;

                    toTarget.y = 0f;

                    bool inRange =
                        math.lengthsq(toTarget) <= attackRangeSq;

                    combat.InAttackRange = inRange;
                    combat.TargetPos = inRange ? targetPoint : float3.zero;

                    if (inRange)
                    {
                        combat.AttackTimer += dt;

                        if (attackParams.AttackCooldown > 0f)
                        {
                            combat.AttackTimer = math.fmod(
                                combat.AttackTimer,
                                attackParams.AttackCooldown);
                        }
                    }
                    else
                    {
                        combat.AttackTimer = 0f;
                    }

                    combatStates[i] = combat;
                }
            }

            private static void ClearCombatStates(
                DynamicBuffer<SoldierCombatState> combatStates,
                int count)
            {
                for (int i = 0; i < count; i++)
                {
                    SoldierCombatState combat = combatStates[i];

                    combat.InAttackRange = false;
                    combat.TargetPos = float3.zero;
                    combat.AttackTimer = 0f;

                    combatStates[i] = combat;
                }
            }

            private bool TryGetTargetFootprint(
                Entity selfEntity,
                in DetectionTarget detectionTarget,
                out float3 targetPosition,
                out quaternion targetRotation,
                out SwarmParams targetSwarmParams)
            {
                targetPosition = detectionTarget.TargetPos;
                targetRotation = quaternion.identity;
                targetSwarmParams = default;

                Entity targetEntity =
                    detectionTarget.CenterTarget;

                if (targetEntity == Entity.Null)
                {
                    return false;
                }

                if (targetEntity == selfEntity)
                {
                    return false;
                }

                if (!TargetTransformLookup.HasComponent(targetEntity))
                {
                    return false;
                }

                if (!TargetSwarmParamsLookup.HasComponent(targetEntity))
                {
                    return false;
                }

                LocalTransform targetTransform =
                    TargetTransformLookup[targetEntity];

                targetPosition =
                    targetTransform.Position;

                targetRotation =
                    targetTransform.Rotation;

                targetSwarmParams =
                    TargetSwarmParamsLookup[targetEntity];

                return true;
            }

            // ======================================================
            //  Footprint Helpers
            // ======================================================

            /// <summary>
            /// 현재 병사 월드 위치에서 상대 Unit 군체 Footprint의 가장 가까운 경계점을 계산한다.
            ///
            /// 상대 Unit을 점이 아니라 회전된 직사각형 영역으로 간주한다.
            /// 병사가 영역 밖에 있으면 가장 가까운 영역 지점을 사용하고,
            /// 병사가 영역 안에 들어와 있으면 가장 가까운 경계면을 사용한다.
            /// </summary>
            private static float3 GetClosestPointOnTargetFootprintBoundary(
                float3 selfWorldPos,
                float3 targetPosition,
                quaternion targetRotation,
                in SwarmParams targetSwarmParams,
                float padding)
            {
                GetFormationHalfExtents(
                    in targetSwarmParams,
                    out float halfWidth,
                    out float halfDepth);

                halfWidth += math.max(0f, padding);
                halfDepth += math.max(0f, padding);

                if (halfWidth <= 0.0001f && halfDepth <= 0.0001f)
                {
                    return targetPosition;
                }

                float3 toSelfWorld =
                    selfWorldPos -
                    targetPosition;

                toSelfWorld.y = 0f;

                quaternion invTargetRot =
                    math.inverse(targetRotation);

                float3 selfInTargetLocal =
                    math.rotate(
                        invTargetRot,
                        toSelfWorld);

                selfInTargetLocal.y = 0f;

                float clampedX = math.clamp(
                    selfInTargetLocal.x,
                    -halfWidth,
                    halfWidth);

                float clampedZ = math.clamp(
                    selfInTargetLocal.z,
                    -halfDepth,
                    halfDepth);

                bool insideX =
                    selfInTargetLocal.x >= -halfWidth &&
                    selfInTargetLocal.x <= halfWidth;

                bool insideZ =
                    selfInTargetLocal.z >= -halfDepth &&
                    selfInTargetLocal.z <= halfDepth;

                bool insideFootprint =
                    insideX &&
                    insideZ;

                float3 targetLocalPoint =
                    new float3(
                        clampedX,
                        0f,
                        clampedZ);

                if (insideFootprint)
                {
                    float distToRight =
                        halfWidth -
                        selfInTargetLocal.x;

                    float distToLeft =
                        selfInTargetLocal.x +
                        halfWidth;

                    float distToFront =
                        halfDepth -
                        selfInTargetLocal.z;

                    float distToBack =
                        selfInTargetLocal.z +
                        halfDepth;

                    float minDist = distToRight;
                    int edge = 0;

                    if (distToLeft < minDist)
                    {
                        minDist = distToLeft;
                        edge = 1;
                    }

                    if (distToFront < minDist)
                    {
                        minDist = distToFront;
                        edge = 2;
                    }

                    if (distToBack < minDist)
                    {
                        edge = 3;
                    }

                    if (edge == 0)
                    {
                        targetLocalPoint.x = halfWidth;
                        targetLocalPoint.z = selfInTargetLocal.z;
                    }
                    else if (edge == 1)
                    {
                        targetLocalPoint.x = -halfWidth;
                        targetLocalPoint.z = selfInTargetLocal.z;
                    }
                    else if (edge == 2)
                    {
                        targetLocalPoint.x = selfInTargetLocal.x;
                        targetLocalPoint.z = halfDepth;
                    }
                    else
                    {
                        targetLocalPoint.x = selfInTargetLocal.x;
                        targetLocalPoint.z = -halfDepth;
                    }
                }

                float3 targetWorldPoint =
                    targetPosition +
                    math.rotate(
                        targetRotation,
                        targetLocalPoint);

                targetWorldPoint.y =
                    targetPosition.y;

                return targetWorldPoint;
            }

            private static void GetFormationHalfExtents(
                in SwarmParams swarmParams,
                out float halfWidth,
                out float halfDepth)
            {
                int soldierCount =
                    math.max(
                        1,
                        swarmParams.SoldierCount);

                int columns =
                    math.max(
                        1,
                        swarmParams.Columns);

                int rows =
                    (int)math.ceil(
                        (float)soldierCount /
                        columns);

                float spacingX =
                    math.max(
                        0f,
                        swarmParams.SpacingX);

                float spacingZ =
                    math.max(
                        0f,
                        swarmParams.SpacingZ);

                halfWidth =
                    math.max(
                        0.05f,
                        (columns - 1) *
                        spacingX *
                        0.5f);

                halfDepth =
                    math.max(
                        0.05f,
                        (rows - 1) *
                        spacingZ *
                        0.5f);

                float jitterPadding =
                    math.max(
                        0f,
                        swarmParams.BaseJitter);

                halfWidth += jitterPadding;
                halfDepth += jitterPadding;
            }
        }
    }
}
