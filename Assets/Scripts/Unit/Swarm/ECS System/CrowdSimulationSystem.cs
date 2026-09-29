using Detection;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Swarm
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(UnitSimulationSystem))]
    [BurstCompile]
    internal partial struct CrowdSimulationSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            var job = new CrowdSimulationJob
            {
                Dt = SystemAPI.Time.DeltaTime,
                TargetTransformLookup = SystemAPI.GetComponentLookup<LocalTransform>(true),
                TargetSwarmParamsLookup = SystemAPI.GetComponentLookup<SwarmParams>(true)
            };

            state.Dependency = job.ScheduleParallel(state.Dependency);
        }

        [BurstCompile]
        private partial struct CrowdSimulationJob : IJobEntity
        {
            public float Dt;

            [ReadOnly] public ComponentLookup<LocalTransform> TargetTransformLookup;
            [ReadOnly] public ComponentLookup<SwarmParams> TargetSwarmParamsLookup;

            private void Execute(
                Entity entity,
                in LocalTransform lt,
                in SwarmParams swarmParams,
                in SwarmCrowdParams crowd,
                in SwarmStateTuning tuning,
                in SwarmEngagementParams engagementParams,
                in SwarmAnimationApproximationTuning animationTuning,
                in FormationTurnParams turnParams,
                in UnitFsm fsm,
                in UnitLocomotion locomotion,
                in UnitAnimationReference animationRef,
                in UnitVelocity unitVel,
                in FormationSteering steering,
                in FormationFrame formationFrame,
                in DetectionTarget detectionTarget,
                in DynamicBuffer<SoldierSlot> slots,
                ref DynamicBuffer<SoldierVariance> vars,
                ref DynamicBuffer<SoldierAgent> agents)
            {
                int count = math.min(
                    agents.Length,
                    math.min(slots.Length, vars.Length));

                if (count <= 0)
                {
                    return;
                }

                bool isLargeTurn =
                    formationFrame.TurnMode == FormationTurnMode.LeftFace ||
                    formationFrame.TurnMode == FormationTurnMode.RightFace ||
                    formationFrame.TurnMode == FormationTurnMode.AboutFace;

                bool isAboutFace =
                    formationFrame.TurnMode == FormationTurnMode.AboutFace;

                float tightness =
                    fsm.State == UnitState.Moving
                        ? tuning.MovingTightness
                        : fsm.State == UnitState.Engaging
                            ? tuning.EngagingTightness
                            : fsm.State == UnitState.Retreating
                                ? tuning.RetreatingTightness
                                : tuning.IdleTightness;

                float locomotionVelocityScale = GetStateTuningValue(
                    locomotion.State,
                    animationTuning.IdleVelocityScale,
                    animationTuning.WalkVelocityScale,
                    animationTuning.RunVelocityScale);

                float formationVelocityScale =
                    locomotionVelocityScale;

                float settleLerpScale =
                    GetSettleLerpScale(locomotion.State) *
                    GetStateTuningValue(
                        locomotion.State,
                        animationTuning.IdleSettleScale,
                        animationTuning.WalkSettleScale,
                        animationTuning.RunSettleScale);

                float personalSpace =
                    math.max(0f, crowd.PersonalSpace);

                if (isLargeTurn)
                {
                    personalSpace *= math.max(
                        0f,
                        turnParams.PersonalSpaceScaleDuringTurn);
                }

                float personalSpaceSq =
                    personalSpace *
                    personalSpace;

                float separationWeight =
                    math.max(0f, crowd.SeparationWeight);

                if (isLargeTurn)
                {
                    separationWeight *= math.max(
                        0f,
                        turnParams.SeparationScaleDuringTurn);
                }

                float slotFollowDuringTurn =
                    math.saturate(
                        turnParams.SlotFollowScaleDuringTurn);

                float turnDir =
                    GetTurnDirection(
                        formationFrame.TurnMode,
                        steering.AngularVelocity);

                float absOmega =
                    math.max(
                        math.abs(steering.AngularVelocity),
                        0.01f);

                float swarmHalfWidth =
                    math.max(1, swarmParams.Columns) *
                    math.max(0f, swarmParams.SpacingX) *
                    0.5f;

                float R =
                    math.max(
                        swarmHalfWidth * 1.5f,
                        10.0f / absOmega);

                quaternion formationRotation =
                    quaternion.RotateY(formationFrame.CurrentYaw);

                bool hasCombatTarget = TryGetCombatTarget(
                    entity,
                    in fsm,
                    in detectionTarget,
                    out LocalTransform targetTransform,
                    out SwarmParams targetSwarmParams);

                // ==================================================
                // [0] Unit Root 이동 보정
                // ==================================================
                //
                // SoldierAgent.CurrentLocalPos는 Unit root 기준의 월드 오프셋처럼 쓰인다.
                // 따라서 Unit root가 이동한 만큼 병사 오프셋을 반대로 보정한다.
                // 이 작업을 모든 병사에게 먼저 적용해야 이후 분리 계산이 같은 기준 위치를 본다.

                float3 anchorDeltaWorld =
                    unitVel.Value *
                    Dt;

                for (int i = 0; i < count; i++)
                {
                    SoldierAgent agent = agents[i];
                    agent.CurrentLocalPos -= anchorDeltaWorld;
                    agents[i] = agent;
                }

                // ==================================================
                // [1] 속도 / Facing / 애니메이션 위상 계산
                // ==================================================
                //
                // 이 루프에서는 CurrentLocalPos를 직접 이동시키지 않는다.
                // 모든 병사가 같은 프레임의 위치를 기준으로 separation을 계산하게 하기 위함이다.

                for (int i = 0; i < count; i++)
                {
                    SoldierAgent agent = agents[i];
                    SoldierSlot slot = slots[i];
                    SoldierVariance variance = vars[i];

                    float3 effectiveSlot =
                        GetEffectiveSlotLocalPos(
                            slot.BaseLocalPos,
                            formationFrame.SlotParity);

                    float theta =
                        isAboutFace
                            ? 0f
                            : effectiveSlot.x *
                              turnDir /
                              R;

                    float3 curvedSlot =
                        isAboutFace
                            ? effectiveSlot
                            : new float3(
                                math.sin(theta) * R * turnDir,
                                0f,
                                math.cos(theta) * R - R + effectiveSlot.z);

                    float wheelingWeight =
                        isAboutFace
                            ? 0f
                            : fsm.WheelingWeight;

                    float3 targetLocalPos =
                        math.lerp(
                            effectiveSlot,
                            curvedSlot,
                            wheelingWeight);

                    float3 targetOffset =
                        math.rotate(
                            formationRotation,
                            targetLocalPos);

                    // 큰 방향 전환 중에는 새 슬롯을 강하게 추적하지 않는다.
                    // 특히 AboutFace에서는 병사 위치를 유지한 채 FacingYaw만 먼저 돌리는 것이 자연스럽다.
                    if (isLargeTurn)
                    {
                        targetOffset =
                            math.lerp(
                                agent.CurrentLocalPos,
                                targetOffset,
                                slotFollowDuringTurn);
                    }

                    bool hasCombatFacing = false;
                    float3 combatFacingDir = float3.zero;

                    // ==================================================
                    //  전투 상태 병사 전진 보정
                    // ==================================================
                    //
                    // 큰 방향 전환 중에는 전투 전진보다 방향 전환 안정성이 우선이다.
                    // 따라서 AboutFace / LeftFace / RightFace 중에는 전투 전진 보정을 끈다.

                    if (hasCombatTarget && !isLargeTurn)
                    {
                        float3 selfSoldierWorldPos =
                            lt.Position +
                            agent.CurrentLocalPos;

                        float3 targetFootprintPoint =
                            GetClosestPointOnTargetFootprintBoundary(
                                selfSoldierWorldPos,
                                in targetTransform,
                                in targetSwarmParams,
                                math.max(0f, engagementParams.TargetFootprintPadding));

                        float3 toCombatTarget =
                            targetFootprintPoint -
                            selfSoldierWorldPos;

                        toCombatTarget.y = 0f;

                        float combatDistSq =
                            math.lengthsq(toCombatTarget);

                        if (combatDistSq > 0.0001f)
                        {
                            float combatDist =
                                math.sqrt(combatDistSq);

                            combatFacingDir =
                                toCombatTarget /
                                combatDist;

                            hasCombatFacing = true;

                            float frontAdvanceWeight =
                                math.lerp(
                                    math.saturate(engagementParams.RearAdvanceScale),
                                    math.max(0f, engagementParams.FrontAdvanceScale),
                                    math.saturate(slot.Frontness));

                            float slotRelease =
                                1f -
                                math.saturate(engagementParams.SlotHoldStrength);

                            float maxAdvance =
                                math.max(0f, engagementParams.MaxEngageAdvance) *
                                frontAdvanceWeight *
                                slotRelease;

                            float advanceAmount =
                                math.min(
                                    maxAdvance,
                                    combatDist);

                            float3 engageAdvanceOffset =
                                combatFacingDir *
                                advanceAmount;

                            float lateralNoise =
                                GetSignedHash01(
                                    variance.Seed,
                                    (uint)i);

                            float3 sideDir =
                                new float3(
                                    combatFacingDir.z,
                                    0f,
                                    -combatFacingDir.x);

                            float lateralAmount =
                                lateralNoise *
                                math.max(0f, engagementParams.LateralFreedom) *
                                math.saturate(slot.Frontness) *
                                slotRelease;

                            float3 lateralOffset =
                                sideDir *
                                lateralAmount;

                            targetOffset +=
                                engageAdvanceOffset +
                                lateralOffset;
                        }
                    }

                    float3 toTarget =
                        targetOffset -
                        agent.CurrentLocalPos;

                    float dist =
                        math.length(toTarget);

                    float radiusRatio =
                        isAboutFace
                            ? 1f
                            : (R + effectiveSlot.x * turnDir) / R;

                    float speedScale =
                        math.lerp(
                            1.0f,
                            radiusRatio,
                            wheelingWeight * 0.8f);

                    float gainScale =
                        math.lerp(
                            1.0f,
                            radiusRatio,
                            wheelingWeight * 0.4f);

                    float frontnessFactor =
                        math.lerp(
                            0.8f,
                            1.2f,
                            slot.Frontness);

                    float pullMag =
                        crowd.FollowGain *
                        tightness *
                        gainScale *
                        frontnessFactor;

                    if (hasCombatTarget && !isLargeTurn)
                    {
                        pullMag *=
                            math.max(
                                0.1f,
                                engagementParams.EngageMoveStrength);
                    }

                    if (isLargeTurn)
                    {
                        pullMag *=
                            math.max(
                                0f,
                                slotFollowDuringTurn);
                    }

                    float3 pullForce =
                        dist > 0.01f
                            ? toTarget /
                              dist *
                              math.min(dist, 2.0f) *
                              pullMag
                            : float3.zero;

                    float3 sepForce =
                        CalculateSeparationForce(
                            i,
                            count,
                            personalSpaceSq,
                            personalSpace,
                            in agents);

                    float3 desiredVel =
                        pullForce +
                        sepForce *
                        separationWeight;

                    desiredVel *=
                        formationVelocityScale;

                    if (isLargeTurn)
                    {
                        desiredVel *=
                            isAboutFace
                                ? math.saturate(turnParams.AboutFaceMoveSpeedScale)
                                : math.saturate(turnParams.LargeTurnMoveSpeedScale);
                    }

                    float individualMax =
                        crowd.MaxLocalSpeed *
                        variance.SpeedMul *
                        speedScale *
                        formationVelocityScale;

                    if (isLargeTurn)
                    {
                        individualMax *=
                            isAboutFace
                                ? math.max(0.05f, math.saturate(turnParams.AboutFaceMoveSpeedScale))
                                : math.max(0.1f, math.saturate(turnParams.LargeTurnMoveSpeedScale));
                    }

                    if (math.lengthsq(desiredVel) > individualMax * individualMax)
                    {
                        desiredVel =
                            math.normalize(desiredVel) *
                            individualMax;
                    }

                    float lerpT =
                        math.saturate(
                            Dt *
                            (5f / math.max(0.1f, variance.MoveDelay)) *
                            settleLerpScale);

                    agent.Velocity =
                        math.lerp(
                            agent.Velocity,
                            desiredVel,
                            lerpT);

                    float targetPhaseSpeed =
                        GetTargetPhaseSpeed(
                            locomotion.State,
                            animationRef.MotionSpeed,
                            animationRef.Speed) *
                        GetStateTuningValue(
                            locomotion.State,
                            animationTuning.IdlePhaseScale,
                            animationTuning.WalkPhaseScale,
                            animationTuning.RunPhaseScale);

                    if (isLargeTurn)
                    {
                        targetPhaseSpeed *=
                            0.35f;
                    }

                    agent.AnimPhaseSpeed =
                        math.lerp(
                            agent.AnimPhaseSpeed,
                            targetPhaseSpeed * variance.SpeedMul,
                            lerpT);

                    variance.Phase =
                        math.fmod(
                            variance.Phase +
                            agent.AnimPhaseSpeed *
                            Dt,
                            2f * math.PI);

                    if (variance.Phase < 0f)
                    {
                        variance.Phase +=
                            2f * math.PI;
                    }

                    // ==================================================
                    //  FacingYaw 계산
                    // ==================================================
                    //
                    // 우선순위:
                    // 1. 큰 방향 전환 중
                    //    → FormationFrame.TargetYaw 방향으로 각 병사가 제자리 회전
                    //
                    // 2. 전투 상태 + 상대 Footprint 방향 있음
                    //    → 상대 Footprint 경계점 방향
                    //
                    // 3. 이동 중
                    //    → 병사 자신의 이동 방향
                    //
                    // 4. 정지 중
                    //    → FormationFrame.CurrentYaw 기준 대열 방향

                    if (isLargeTurn)
                    {
                        float facingT =
                            math.saturate(
                                Dt *
                                math.max(
                                    0f,
                                    turnParams.SoldierFacingTurnSpeed));

                        agent.FacingYaw =
                            LerpAngleRadians(
                                agent.FacingYaw,
                                formationFrame.TargetYaw,
                                facingT);
                    }
                    else if (hasCombatFacing)
                    {
                        float targetYaw =
                            math.atan2(
                                combatFacingDir.x,
                                combatFacingDir.z);

                        float facingT =
                            math.saturate(
                                Dt *
                                math.max(
                                    0f,
                                    engagementParams.FacingSharpness));

                        agent.FacingYaw =
                            LerpAngleRadians(
                                agent.FacingYaw,
                                targetYaw,
                                facingT);
                    }
                    else if (math.lengthsq(agent.Velocity) > 0.01f)
                    {
                        float3 dir =
                            math.normalize(agent.Velocity);

                        agent.FacingYaw =
                            math.atan2(
                                dir.x,
                                dir.z);
                    }
                    else
                    {
                        SetDefaultFacingYaw(
                            ref agent,
                            formationFrame.CurrentYaw,
                            theta,
                            turnDir);
                    }

                    vars[i] = variance;
                    agents[i] = agent;
                }

                // ==================================================
                // [2] 위치 갱신
                // ==================================================
                //
                // 모든 병사의 Velocity 계산이 끝난 뒤 위치를 갱신한다.
                // 이렇게 하면 같은 프레임 안에서 병사별 separation 계산 기준이 섞이지 않는다.

                for (int i = 0; i < count; i++)
                {
                    SoldierAgent agent = agents[i];
                    SoldierVariance variance = vars[i];

                    float swayMag =
                        GetSwayMagnitude(
                            locomotion.State,
                            animationRef.MotionSpeed,
                            agent.AnimPhaseSpeed) *
                        GetStateTuningValue(
                            locomotion.State,
                            animationTuning.IdleSwayScale,
                            animationTuning.WalkSwayScale,
                            animationTuning.RunSwayScale);

                    if (isLargeTurn)
                    {
                        swayMag *=
                            0.25f;
                    }

                    float3 sway =
                        new float3(
                            math.sin(variance.Phase) * swayMag,
                            0f,
                            math.cos(variance.Phase * 0.5f) * swayMag * 0.5f);

                    agent.CurrentLocalPos +=
                        agent.Velocity *
                        Dt +
                        sway *
                        Dt;

                    agents[i] = agent;
                }

                // ==================================================
                // [3] 큰 방향 전환 중 위치 Projection
                // ==================================================
                //
                // separation force는 속도 기반이므로 이미 겹친 병사를 즉시 풀어내기 어렵다.
                // AboutFace / 큰 방향 전환 중에는 1~2회 위치 보정을 넣어 겹침을 줄인다.

                if (isLargeTurn &&
                    turnParams.PositionProjectionIterations > 0 &&
                    turnParams.PositionProjectionStrength > 0f)
                {
                    ApplyPositionProjection(
                        count,
                        personalSpace,
                        math.saturate(turnParams.PositionProjectionStrength),
                        math.max(0, turnParams.PositionProjectionIterations),
                        agents);
                }
            }

            // ======================================================
            //  Slot / Turn Helpers
            // ======================================================

            private static float3 GetEffectiveSlotLocalPos(
                float3 baseLocalPos,
                int slotParity)
            {
                if (slotParity < 0)
                {
                    baseLocalPos.x = -baseLocalPos.x;
                    baseLocalPos.z = -baseLocalPos.z;
                }

                return baseLocalPos;
            }

            private static float GetTurnDirection(
                FormationTurnMode turnMode,
                float angularVelocity)
            {
                if (turnMode == FormationTurnMode.LeftFace)
                {
                    return -1f;
                }

                if (turnMode == FormationTurnMode.RightFace)
                {
                    return 1f;
                }

                float turnDir =
                    math.sign(angularVelocity);

                if (turnDir == 0f)
                {
                    turnDir = 1f;
                }

                return turnDir;
            }

            private static void SetDefaultFacingYaw(
                ref SoldierAgent agent,
                float formationYaw,
                float theta,
                float turnDir)
            {
                float yaw =
                    formationYaw +
                    theta *
                    turnDir;

                agent.FacingYaw =
                    yaw;
            }

            // ======================================================
            //  Separation / Projection
            // ======================================================

            private static float3 CalculateSeparationForce(
                int selfIndex,
                int count,
                float personalSpaceSq,
                float personalSpace,
                in DynamicBuffer<SoldierAgent> agents)
            {
                float3 sepForce =
                    float3.zero;

                SoldierAgent selfAgent =
                    agents[selfIndex];

                for (int j = 0; j < count; j++)
                {
                    if (selfIndex == j)
                    {
                        continue;
                    }

                    float3 diff =
                        selfAgent.CurrentLocalPos -
                        agents[j].CurrentLocalPos;

                    diff.y = 0f;

                    float dSq =
                        math.lengthsq(diff);

                    if (dSq < personalSpaceSq && dSq > 0.001f)
                    {
                        float d =
                            math.sqrt(dSq);

                        sepForce +=
                            diff /
                            d *
                            (personalSpace - d);
                    }
                    else if (dSq <= 0.001f)
                    {
                        float3 fallbackDir =
                            GetPairFallbackDirection(
                                (uint)selfIndex,
                                (uint)j);

                        sepForce +=
                            fallbackDir *
                            personalSpace;
                    }
                }

                return sepForce;
            }

            private static void ApplyPositionProjection(
                int count,
                float personalSpace,
                float strength,
                int iterations,
                DynamicBuffer<SoldierAgent> agents)
            {
                float minDistance =
                    math.max(0f, personalSpace);

                float minDistanceSq =
                    minDistance *
                    minDistance;

                if (minDistance <= 0f)
                {
                    return;
                }

                for (int iter = 0; iter < iterations; iter++)
                {
                    for (int i = 0; i < count; i++)
                    {
                        for (int j = i + 1; j < count; j++)
                        {
                            SoldierAgent a =
                                agents[i];

                            SoldierAgent b =
                                agents[j];

                            float3 diff =
                                a.CurrentLocalPos -
                                b.CurrentLocalPos;

                            diff.y = 0f;

                            float dSq =
                                math.lengthsq(diff);

                            if (dSq >= minDistanceSq)
                            {
                                continue;
                            }

                            float3 dir;
                            float d;

                            if (dSq > 0.001f)
                            {
                                d =
                                    math.sqrt(dSq);

                                dir =
                                    diff /
                                    d;
                            }
                            else
                            {
                                d =
                                    0f;

                                dir =
                                    GetPairFallbackDirection(
                                        (uint)i,
                                        (uint)j);
                            }

                            float penetration =
                                minDistance -
                                d;

                            float3 correction =
                                dir *
                                penetration *
                                0.5f *
                                strength;

                            a.CurrentLocalPos +=
                                correction;

                            b.CurrentLocalPos -=
                                correction;

                            agents[i] = a;
                            agents[j] = b;
                        }
                    }
                }
            }

            private static float3 GetPairFallbackDirection(
                uint a,
                uint b)
            {
                uint x =
                    a * 73856093u ^
                    b * 19349663u ^
                    0x9E3779B9u;

                x ^= x >> 16;
                x *= 0x7FEB352Du;
                x ^= x >> 15;
                x *= 0x846CA68Bu;
                x ^= x >> 16;

                float angle =
                    ((x & 0x00FFFFFFu) / 16777215f) *
                    2f *
                    math.PI;

                return new float3(
                    math.sin(angle),
                    0f,
                    math.cos(angle));
            }

            // ======================================================
            //  Combat Target Helpers
            // ======================================================

            private bool TryGetCombatTarget(
                Entity selfEntity,
                in UnitFsm fsm,
                in DetectionTarget detectionTarget,
                out LocalTransform targetTransform,
                out SwarmParams targetSwarmParams)
            {
                targetTransform = default;
                targetSwarmParams = default;

                if (fsm.State != UnitState.Engaging)
                {
                    return false;
                }

                if (!detectionTarget.HasTarget)
                {
                    return false;
                }

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

                targetTransform =
                    TargetTransformLookup[targetEntity];

                targetSwarmParams =
                    TargetSwarmParamsLookup[targetEntity];

                return true;
            }

            private static float3 GetClosestPointOnTargetFootprintBoundary(
                float3 selfWorldPos,
                in LocalTransform targetTransform,
                in SwarmParams targetSwarmParams,
                float padding)
            {
                GetFormationHalfExtents(
                    in targetSwarmParams,
                    out float halfWidth,
                    out float halfDepth);

                halfWidth +=
                    math.max(0f, padding);

                halfDepth +=
                    math.max(0f, padding);

                if (halfWidth <= 0.0001f && halfDepth <= 0.0001f)
                {
                    return targetTransform.Position;
                }

                float3 toSelfWorld =
                    selfWorldPos -
                    targetTransform.Position;

                toSelfWorld.y = 0f;

                quaternion invTargetRot =
                    math.inverse(targetTransform.Rotation);

                float3 selfInTargetLocal =
                    math.rotate(
                        invTargetRot,
                        toSelfWorld);

                selfInTargetLocal.y = 0f;

                float clampedX =
                    math.clamp(
                        selfInTargetLocal.x,
                        -halfWidth,
                        halfWidth);

                float clampedZ =
                    math.clamp(
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

                    float minDist =
                        distToRight;

                    int edge =
                        0;

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
                    targetTransform.Position +
                    math.rotate(
                        targetTransform.Rotation,
                        targetLocalPoint);

                targetWorldPoint.y =
                    targetTransform.Position.y;

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

                halfWidth +=
                    jitterPadding;

                halfDepth +=
                    jitterPadding;
            }

            // ======================================================
            //  Math Helpers
            // ======================================================

            private static float LerpAngleRadians(
                float from,
                float to,
                float t)
            {
                float delta =
                    math.atan2(
                        math.sin(to - from),
                        math.cos(to - from));

                return
                    from +
                    delta *
                    math.saturate(t);
            }

            private static float GetSignedHash01(
                uint seed,
                uint index)
            {
                uint x =
                    seed ^
                    (index + 0x9E3779B9u);

                x ^= x >> 16;
                x *= 0x7FEB352Du;
                x ^= x >> 15;
                x *= 0x846CA68Bu;
                x ^= x >> 16;

                float normalized =
                    (x & 0x00FFFFFFu) /
                    16777215f;

                return
                    normalized *
                    2f -
                    1f;
            }

            // ======================================================
            //  Animation Approximation Helpers
            // ======================================================

            private static float GetTargetPhaseSpeed(
                LocomotionState locomotionState,
                float motionSpeed,
                float referenceSpeed)
            {
                if (locomotionState == LocomotionState.Idle)
                {
                    return 0f;
                }

                if (locomotionState == LocomotionState.Walk)
                {
                    return math.max(
                        2.1f,
                        referenceSpeed * 1.1f +
                        motionSpeed * 1.5f);
                }

                return math.max(
                    4.75f,
                    referenceSpeed * 1.1f +
                    motionSpeed * 2.75f);
            }

            private static float GetSwayMagnitude(
                LocomotionState locomotionState,
                float motionSpeed,
                float animPhaseSpeed)
            {
                if (locomotionState == LocomotionState.Idle)
                {
                    return 0.0015f;
                }

                if (locomotionState == LocomotionState.Walk)
                {
                    return math.min(
                        0.028f,
                        0.007f +
                        motionSpeed * 0.022f +
                        animPhaseSpeed * 0.0015f);
                }

                return math.min(
                    0.055f,
                    0.016f +
                    motionSpeed * 0.03f +
                    animPhaseSpeed * 0.0025f);
            }

            private static float GetSettleLerpScale(
                LocomotionState locomotionState)
            {
                if (locomotionState == LocomotionState.Idle)
                {
                    return 1.75f;
                }

                if (locomotionState == LocomotionState.Walk)
                {
                    return 0.95f;
                }

                return 1.15f;
            }

            private static float GetStateTuningValue(
                LocomotionState locomotionState,
                float idle,
                float walk,
                float run)
            {
                if (locomotionState == LocomotionState.Idle)
                {
                    return idle;
                }

                if (locomotionState == LocomotionState.Walk)
                {
                    return walk;
                }

                return run;
            }
        }
    }
}
