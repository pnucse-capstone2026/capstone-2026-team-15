using Detection;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace Swarm
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(GlobalStrategySystem))]
    [BurstCompile]
    public partial struct UnitSimulationSystem : ISystem
    {
        private EntityQuery _targetSnapshotQuery;

        public void OnCreate(ref SystemState state)
        {
            _targetSnapshotQuery = state.GetEntityQuery(
                ComponentType.ReadOnly<UnitTag>(),
                ComponentType.ReadOnly<LocalTransform>(),
                ComponentType.ReadOnly<SwarmParams>());

            state.RequireForUpdate(_targetSnapshotQuery);
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            int targetCount =
                math.max(
                    1,
                    _targetSnapshotQuery.CalculateEntityCount());

            var targetSnapshots =
                new NativeParallelHashMap<Entity, TargetUnitSnapshot>(
                    targetCount,
                    Allocator.TempJob);

            var buildSnapshotJob = new BuildTargetSnapshotJob
            {
                TargetSnapshots = targetSnapshots.AsParallelWriter()
            };

            JobHandle buildHandle =
                buildSnapshotJob.ScheduleParallel(
                    _targetSnapshotQuery,
                    state.Dependency);

            var moveJob = new UnitMoveJob
            {
                Dt = SystemAPI.Time.DeltaTime,
                MapWidth = 1000f,
                MapHeight = 1000f,
                TargetSnapshots = targetSnapshots
            };

            JobHandle moveHandle =
                moveJob.ScheduleParallel(buildHandle);

            state.Dependency =
                targetSnapshots.Dispose(moveHandle);
        }

        private struct TargetUnitSnapshot
        {
            public float3 Position;
            public quaternion Rotation;
            public SwarmParams SwarmParams;
        }

        [BurstCompile]
        private partial struct BuildTargetSnapshotJob : IJobEntity
        {
            public NativeParallelHashMap<Entity, TargetUnitSnapshot>.ParallelWriter TargetSnapshots;

            private void Execute(
                Entity entity,
                in UnitTag tag,
                in LocalTransform lt,
                in SwarmParams swarmParams)
            {
                TargetSnapshots.TryAdd(entity, new TargetUnitSnapshot
                {
                    Position = lt.Position,
                    Rotation = lt.Rotation,
                    SwarmParams = swarmParams
                });
            }
        }

        [BurstCompile]
        private partial struct UnitMoveJob : IJobEntity
        {
            public float Dt;
            public float MapWidth;
            public float MapHeight;

            [ReadOnly]
            public NativeParallelHashMap<Entity, TargetUnitSnapshot> TargetSnapshots;

            private void Execute(
                Entity entity,
                ref LocalTransform lt,
                ref UnitVelocity vel,
                ref UnitPreviousPos prevPos,
                ref UnitPreviousRot prevRot,
                ref UnitFsm fsm,
                ref UnitLocomotion locomotion,
                ref UnitAnimationReference animationRef,
                ref FormationSteering steering,
                ref FormationFrame formationFrame,
                in UnitAnimationReferenceCalibration calibration,
                ref UnitMoveTarget cmdTarget,
                in DetectionTarget detTarget,
                in UnitMoveParams moveParams,
                in UnitAttackParams attackParams,
                in UnitAttackMovePolicy attackMovePolicy,
                in UnitContactParams contactParams,
                in FormationTurnParams turnParams,
                in SwarmParams swarmParams,
                in UnitTag tag)
            {
                EnsureFormationFrameInitialized(
                    ref formationFrame,
                    lt.Rotation);

                prevPos.Value = lt.Position;
                prevRot.Value = lt.Rotation;

                bool hasCommandTarget = cmdTarget.HasTarget;
                bool hasDetectionTarget = detTarget.HasTarget;

                if (!hasDetectionTarget && !hasCommandTarget)
                {
                    SetIdle(
                        ref vel,
                        ref fsm,
                        ref locomotion,
                        ref animationRef,
                        ref steering,
                        ref formationFrame,
                        in calibration,
                        Dt);

                    return;
                }

                bool combatMove = hasDetectionTarget;

                float3 targetPos;
                float3 faceTargetPos;
                float stopDistance;
                float stopBandWidth;

                if (combatMove)
                {
                    BuildCombatTarget(
                        entity,
                        in lt,
                        in detTarget,
                        in swarmParams,
                        in contactParams,
                        in moveParams,
                        in attackParams,
                        in attackMovePolicy,
                        out targetPos,
                        out faceTargetPos,
                        out stopDistance,
                        out stopBandWidth);
                }
                else
                {
                    targetPos = cmdTarget.Position;
                    faceTargetPos = cmdTarget.Position;
                    stopDistance = math.max(
                        0.001f,
                        moveParams.ArriveRadius);
                    stopBandWidth = 0f;
                }

                float3 toTarget = GetTorusDelta(
                    targetPos,
                    lt.Position,
                    MapWidth,
                    MapHeight);

                toTarget.y = 0f;

                float distSq =
                    math.lengthsq(toTarget);

                float stopThreshold =
                    stopDistance;

                if (combatMove)
                {
                    stopThreshold +=
                        math.max(
                            0f,
                            stopBandWidth);
                }

                stopThreshold =
                    math.max(
                        stopThreshold,
                        moveParams.ArriveRadius);

                float stopThresholdSq =
                    stopThreshold *
                    stopThreshold;

                if (distSq <= stopThresholdSq)
                {
                    if (combatMove)
                    {
                        StopAndFaceTarget(
                            ref lt,
                            ref vel,
                            ref fsm,
                            ref locomotion,
                            ref animationRef,
                            ref steering,
                            ref formationFrame,
                            in calibration,
                            in moveParams,
                            in turnParams,
                            faceTargetPos,
                            Dt,
                            MapWidth,
                            MapHeight);
                    }
                    else
                    {
                        SetIdle(
                            ref vel,
                            ref fsm,
                            ref locomotion,
                            ref animationRef,
                            ref steering,
                            ref formationFrame,
                            in calibration,
                            Dt);
                    }

                    return;
                }

                float3 desiredDirection =
                    math.normalizesafe(
                        toTarget,
                        math.forward(lt.Rotation));

                desiredDirection.y = 0f;

                desiredDirection =
                    math.normalizesafe(
                        desiredDirection,
                        math.forward(lt.Rotation));

                steering.DesiredDirection =
                    desiredDirection;

                fsm.OriMode =
                    OrientationMode.FaceMovement;

                // ==================================================
                // [1] 대열 방향 전환 상태 갱신
                // ==================================================
                //
                // 목표 이동 방향과 현재 FormationFrame.CurrentYaw를 비교하여
                // None / LeftFace / RightFace / AboutFace를 결정한다.
                //
                // AboutFace 중에는 FormationFrame.CurrentYaw를 즉시 돌리지 않고,
                // Turn 완료 시 SlotParity를 반전하여 병사 슬롯 교차를 막는다.

                UpdateFormationTurnFrame(
                    ref formationFrame,
                    in turnParams,
                    desiredDirection,
                    lt.Rotation,
                    Dt);

                // ==================================================
                // [2] 유닛 본체 회전
                // ==================================================

                float3 oldForward =
                    math.normalizesafe(
                        math.forward(lt.Rotation),
                        new float3(0f, 0f, 1f));

                float bodyAngleToDesired =
                    GetUnsignedAngleRadians(
                        oldForward,
                        desiredDirection);

                quaternion targetRot =
                    quaternion.LookRotationSafe(
                        desiredDirection,
                        math.up());

                lt.Rotation =
                    math.slerp(
                        lt.Rotation,
                        targetRot,
                        math.saturate(Dt * moveParams.TurnRate));

                float3 newForward =
                    math.normalizesafe(
                        math.forward(lt.Rotation),
                        new float3(0f, 0f, 1f));

                steering.CurrentForward =
                    newForward;

                float angleDelta =
                    math.acos(
                        math.clamp(
                            math.dot(oldForward, newForward),
                            -1f,
                            1f));

                steering.AngularVelocity =
                    angleDelta /
                    math.max(Dt, 0.0001f);

                float crossY =
                    math.cross(
                        oldForward,
                        newForward).y;

                steering.AngularVelocity *=
                    math.sign(crossY);

                // ==================================================
                // [3] 대열 모드 및 WheelingWeight 갱신
                // ==================================================

                UpdateFormationMode(
                    ref fsm,
                    in formationFrame,
                    steering.AngularVelocity,
                    Dt);

                // ==================================================
                // [4] 속도 결정
                // ==================================================
                //
                // 일반 이동:
                // - Base / 명령 좌표 이동은 Walk
                //
                // 전투 접근:
                // - 적 탐지 접근은 Run
                //
                // 큰 방향 전환:
                // - LeftFace / RightFace는 LargeTurnMoveSpeedScale 적용
                // - AboutFace는 AboutFaceMoveSpeedScale 적용

                float baseDesiredSpeed =
                    combatMove
                        ? moveParams.MaxSpeed
                        : GetCommandWalkSpeed(in moveParams);

                float turnSpeedMultiplier =
                    GetTurnSpeedMultiplier(bodyAngleToDesired);

                float formationTurnMoveScale =
                    GetFormationTurnMoveScale(
                        formationFrame.TurnMode,
                        in turnParams);

                steering.DesiredSpeed =
                    baseDesiredSpeed *
                    turnSpeedMultiplier *
                    formationTurnMoveScale;

                vel.Value =
                    math.lerp(
                        vel.Value,
                        steering.CurrentForward * steering.DesiredSpeed,
                        math.saturate(Dt * moveParams.Accel));

                lt.Position +=
                    vel.Value *
                    Dt;

                // ==================================================
                // [5] FSM / Locomotion / Animation Reference 갱신
                // ==================================================

                float speed =
                    math.length(vel.Value);

                float normalizedSpeed =
                    speed /
                    math.max(
                        moveParams.MaxSpeed,
                        0.0001f);

                locomotion.NormalizedSpeed =
                    math.saturate(normalizedSpeed);

                if (combatMove)
                {
                    locomotion.State =
                        speed <= moveParams.IdleSpeedEpsilon
                            ? LocomotionState.Idle
                            : LocomotionState.Run;

                    fsm.State =
                        locomotion.State == LocomotionState.Idle
                            ? UnitState.Idle
                            : UnitState.Moving;
                }
                else
                {
                    locomotion.State =
                        speed <= moveParams.IdleSpeedEpsilon
                            ? LocomotionState.Idle
                            : LocomotionState.Walk;

                    fsm.State =
                        locomotion.State == LocomotionState.Idle
                            ? UnitState.Idle
                            : UnitState.Moving;
                }

                UpdateAnimationReference(
                    ref animationRef,
                    in locomotion,
                    in moveParams,
                    in calibration);
            }

            // ======================================================
            //  Formation Turn
            // ======================================================

            private static void EnsureFormationFrameInitialized(
                ref FormationFrame frame,
                quaternion unitRotation)
            {
                if (frame.SlotParity != 0)
                {
                    return;
                }

                float initialYaw =
                    GetYawFromRotation(unitRotation);

                frame.CurrentYaw = initialYaw;
                frame.TargetYaw = initialYaw;
                frame.TurnStartYaw = initialYaw;
                frame.TurnProgress = 0f;
                frame.SlotParity = 1;
                frame.TurnMode = FormationTurnMode.None;
            }

            private static void UpdateFormationTurnFrame(
                ref FormationFrame frame,
                in FormationTurnParams turnParams,
                float3 desiredDirection,
                quaternion unitRotation,
                float dt)
            {
                if (math.lengthsq(desiredDirection) <= 0.0001f)
                {
                    return;
                }

                float targetYaw =
                    GetYawFromDirection(desiredDirection);

                frame.TargetYaw =
                    targetYaw;

                if (frame.SlotParity == 0)
                {
                    frame.SlotParity = 1;
                }

                float completeAngleRad =
                    math.radians(
                        math.max(
                            0f,
                            turnParams.TurnCompleteAngleDegrees));

                FormationTurnMode currentMode =
                    frame.TurnMode;

                if (currentMode == FormationTurnMode.None)
                {
                    float currentToTargetRad =
                        math.abs(
                            DeltaAngleRadians(
                                frame.CurrentYaw,
                                targetYaw));

                    float currentToTargetDeg =
                        math.degrees(currentToTargetRad);

                    FormationTurnMode newMode =
                        SelectTurnMode(
                            frame.CurrentYaw,
                            targetYaw,
                            in turnParams);

                    if (newMode != FormationTurnMode.None)
                    {
                        frame.TurnMode = newMode;
                        frame.TurnStartYaw = frame.CurrentYaw;
                        frame.TurnProgress = 0f;
                    }
                    else
                    {
                        frame.CurrentYaw =
                            MoveTowardsAngleRadians(
                                frame.CurrentYaw,
                                targetYaw,
                                math.max(0f, turnParams.FormationYawTurnSpeed) * dt);

                        frame.TurnStartYaw =
                            frame.CurrentYaw;

                        frame.TurnProgress =
                            currentToTargetDeg <= 0.001f
                                ? 1f
                                : 0f;

                        return;
                    }
                }

                if (frame.TurnMode == FormationTurnMode.AboutFace)
                {
                    // AboutFace에서는 병사 슬롯 기준 방향을 즉시 돌리지 않는다.
                    // 병사들은 CrowdSimulationSystem에서 현재 위치를 유지한 채
                    // FacingYaw만 TargetYaw로 회전하게 된다.
                    //
                    // Unit 본체 회전이 목표 방향에 거의 도달하면,
                    // CurrentYaw를 TargetYaw로 바꾸고 SlotParity를 반전한다.
                    //
                    // rotate(oldYaw, slot) == rotate(oldYaw + PI, -slot)
                    // 이 성질을 이용해 병사 슬롯 교차를 방지한다.

                    float bodyYaw =
                        GetYawFromRotation(unitRotation);

                    float remainingRad =
                        math.abs(
                            DeltaAngleRadians(
                                bodyYaw,
                                targetYaw));

                    float totalRad =
                        math.abs(
                            DeltaAngleRadians(
                                frame.TurnStartYaw,
                                targetYaw));

                    frame.TurnProgress =
                        totalRad <= 0.0001f
                            ? 1f
                            : math.saturate(
                                1f - remainingRad / totalRad);

                    if (remainingRad <= completeAngleRad)
                    {
                        frame.CurrentYaw =
                            targetYaw;

                        frame.TargetYaw =
                            targetYaw;

                        frame.TurnStartYaw =
                            targetYaw;

                        frame.TurnProgress =
                            1f;

                        frame.SlotParity =
                            frame.SlotParity >= 0
                                ? -1
                                : 1;

                        frame.TurnMode =
                            FormationTurnMode.None;
                    }

                    return;
                }

                if (frame.TurnMode == FormationTurnMode.LeftFace ||
                    frame.TurnMode == FormationTurnMode.RightFace)
                {
                    float remainingBeforeRad =
                        math.abs(
                            DeltaAngleRadians(
                                frame.CurrentYaw,
                                targetYaw));

                    frame.CurrentYaw =
                        MoveTowardsAngleRadians(
                            frame.CurrentYaw,
                            targetYaw,
                            math.max(0f, turnParams.FormationYawTurnSpeed) * dt);

                    float remainingAfterRad =
                        math.abs(
                            DeltaAngleRadians(
                                frame.CurrentYaw,
                                targetYaw));

                    float totalRad =
                        math.abs(
                            DeltaAngleRadians(
                                frame.TurnStartYaw,
                                targetYaw));

                    frame.TurnProgress =
                        totalRad <= 0.0001f
                            ? 1f
                            : math.saturate(
                                1f - remainingAfterRad / totalRad);

                    if (remainingAfterRad <= completeAngleRad ||
                        remainingAfterRad > remainingBeforeRad + 0.0001f)
                    {
                        frame.CurrentYaw =
                            targetYaw;

                        frame.TargetYaw =
                            targetYaw;

                        frame.TurnStartYaw =
                            targetYaw;

                        frame.TurnProgress =
                            1f;

                        frame.TurnMode =
                            FormationTurnMode.None;
                    }
                }
            }

            private static FormationTurnMode SelectTurnMode(
                float currentYaw,
                float targetYaw,
                in FormationTurnParams turnParams)
            {
                float signedDeltaRad =
                    DeltaAngleRadians(
                        currentYaw,
                        targetYaw);

                float absDeltaDeg =
                    math.degrees(
                        math.abs(signedDeltaRad));

                float aboutFaceThreshold =
                    math.clamp(
                        turnParams.AboutFaceAngleThresholdDegrees,
                        0f,
                        180f);

                float wheelingThreshold =
                    math.clamp(
                        turnParams.WheelingAngleThresholdDegrees,
                        0f,
                        180f);

                if (absDeltaDeg >= aboutFaceThreshold)
                {
                    return FormationTurnMode.AboutFace;
                }

                if (absDeltaDeg >= wheelingThreshold)
                {
                    return signedDeltaRad >= 0f
                        ? FormationTurnMode.RightFace
                        : FormationTurnMode.LeftFace;
                }

                return FormationTurnMode.None;
            }

            private static void UpdateFormationMode(
                ref UnitFsm fsm,
                in FormationFrame frame,
                float angularVelocity,
                float dt)
            {
                if (frame.TurnMode == FormationTurnMode.AboutFace)
                {
                    fsm.FormMode =
                        FormationMode.Marching;

                    fsm.WheelingWeight =
                        math.lerp(
                            fsm.WheelingWeight,
                            0f,
                            math.saturate(dt * 4f));

                    return;
                }

                if (frame.TurnMode == FormationTurnMode.LeftFace ||
                    frame.TurnMode == FormationTurnMode.RightFace)
                {
                    fsm.FormMode =
                        FormationMode.Wheeling;

                    fsm.WheelingWeight =
                        math.lerp(
                            fsm.WheelingWeight,
                            1f,
                            math.saturate(dt * 3f));

                    return;
                }

                float absOmega =
                    math.abs(angularVelocity);

                if (fsm.FormMode == FormationMode.Marching && absOmega > 0.5f)
                {
                    fsm.FormMode =
                        FormationMode.Wheeling;
                }
                else if (fsm.FormMode == FormationMode.Wheeling && absOmega < 0.2f)
                {
                    fsm.FormMode =
                        FormationMode.Marching;
                }

                float targetWheelingWeight =
                    fsm.FormMode == FormationMode.Wheeling
                        ? 1f
                        : 0f;

                fsm.WheelingWeight =
                    math.lerp(
                        fsm.WheelingWeight,
                        targetWheelingWeight,
                        math.saturate(dt * 2.5f));
            }

            private static float GetFormationTurnMoveScale(
                FormationTurnMode mode,
                in FormationTurnParams turnParams)
            {
                if (mode == FormationTurnMode.AboutFace)
                {
                    return math.saturate(
                        turnParams.AboutFaceMoveSpeedScale);
                }

                if (mode == FormationTurnMode.LeftFace ||
                    mode == FormationTurnMode.RightFace)
                {
                    return math.saturate(
                        turnParams.LargeTurnMoveSpeedScale);
                }

                return 1f;
            }

            // ======================================================
            //  Combat Target / Contact
            // ======================================================

            private void BuildCombatTarget(
                Entity selfEntity,
                in LocalTransform selfTransform,
                in DetectionTarget detectionTarget,
                in SwarmParams selfSwarmParams,
                in UnitContactParams contactParams,
                in UnitMoveParams moveParams,
                in UnitAttackParams attackParams,
                in UnitAttackMovePolicy attackMovePolicy,
                out float3 targetPos,
                out float3 faceTargetPos,
                out float stopDistance,
                out float stopBandWidth)
            {
                float3 targetCenter =
                    detectionTarget.TargetPos;

                targetPos =
                    targetCenter;

                faceTargetPos =
                    targetCenter;

                stopDistance =
                    math.max(
                        moveParams.ArriveRadius,
                        attackParams.AttackRange);

                stopBandWidth =
                    math.max(
                        0f,
                        contactParams.ContactBandWidth);

                Entity targetEntity =
                    detectionTarget.CenterTarget;

                TargetUnitSnapshot targetSnapshot =
                    default;

                bool hasTargetSnapshot =
                    targetEntity != Entity.Null &&
                    targetEntity != selfEntity &&
                    TargetSnapshots.TryGetValue(
                        targetEntity,
                        out targetSnapshot);

                if (hasTargetSnapshot)
                {
                    targetCenter =
                        targetSnapshot.Position;

                    targetPos =
                        targetCenter;

                    faceTargetPos =
                        targetCenter;
                }

                if (attackMovePolicy.Policy == AttackMovementPolicy.PreferredRange)
                {
                    BuildPreferredRangeCombatTarget(
                        in selfTransform,
                        targetCenter,
                        in moveParams,
                        in attackParams,
                        in attackMovePolicy,
                        out targetPos,
                        out faceTargetPos,
                        out stopDistance,
                        out stopBandWidth);

                    return;
                }

                if (attackMovePolicy.Policy == AttackMovementPolicy.HoldPosition)
                {
                    BuildHoldPositionCombatTarget(
                        in selfTransform,
                        targetCenter,
                        in moveParams,
                        out targetPos,
                        out faceTargetPos,
                        out stopDistance,
                        out stopBandWidth);

                    return;
                }

                if (!hasTargetSnapshot)
                {
                    return;
                }

                float3 centerDelta =
                    GetTorusDelta(
                        targetSnapshot.Position,
                        selfTransform.Position,
                        MapWidth,
                        MapHeight);

                centerDelta.y = 0f;

                float3 approachDir =
                    math.normalizesafe(
                        centerDelta,
                        math.forward(selfTransform.Rotation));

                approachDir.y = 0f;

                if (math.lengthsq(approachDir) <= 0.0001f)
                {
                    approachDir =
                        math.forward(selfTransform.Rotation);

                    approachDir.y = 0f;

                    approachDir =
                        math.normalizesafe(
                            approachDir,
                            new float3(0f, 0f, 1f));
                }

                float selfSupport =
                    GetFootprintSupportRadius(
                        approachDir,
                        selfTransform.Rotation,
                        in selfSwarmParams);

                float targetSupport =
                    GetFootprintSupportRadius(
                        -approachDir,
                        targetSnapshot.Rotation,
                        in targetSnapshot.SwarmParams);

                float supportSum =
                    selfSupport +
                    targetSupport;

                float allowedOverlapRatio =
                    math.saturate(
                        contactParams.EnemyAllowedOverlapRatio);

                stopDistance =
                    supportSum *
                    (1f - allowedOverlapRatio) +
                    math.max(
                        0f,
                        contactParams.ContactPadding);

                stopDistance =
                    math.max(
                        moveParams.ArriveRadius,
                        stopDistance);

                faceTargetPos =
                    GetClosestPointOnTargetFootprintBoundary(
                        selfTransform.Position,
                        targetSnapshot.Position,
                        targetSnapshot.Rotation,
                        in targetSnapshot.SwarmParams,
                        math.max(
                            0f,
                            contactParams.ContactPadding));
            }

            private void BuildPreferredRangeCombatTarget(
                in LocalTransform selfTransform,
                float3 targetCenter,
                in UnitMoveParams moveParams,
                in UnitAttackParams attackParams,
                in UnitAttackMovePolicy attackMovePolicy,
                out float3 targetPos,
                out float3 faceTargetPos,
                out float stopDistance,
                out float stopBandWidth)
            {
                GetPreferredRangeBounds(
                    in moveParams,
                    in attackParams,
                    in attackMovePolicy,
                    out float minRange,
                    out float maxRange);

                faceTargetPos =
                    targetCenter;

                stopBandWidth =
                    0f;

                float3 toTarget =
                    GetTorusDelta(
                        targetCenter,
                        selfTransform.Position,
                        MapWidth,
                        MapHeight);

                toTarget.y = 0f;

                float distance =
                    math.length(toTarget);

                if (distance > maxRange)
                {
                    targetPos =
                        targetCenter;

                    stopDistance =
                        maxRange;

                    return;
                }

                float3 directionToTarget =
                    math.normalizesafe(
                        toTarget,
                        math.forward(selfTransform.Rotation));

                directionToTarget.y = 0f;

                directionToTarget =
                    math.normalizesafe(
                        directionToTarget,
                        math.forward(selfTransform.Rotation));

                if (distance < minRange)
                {
                    float retreatDistance =
                        minRange -
                        distance +
                        math.max(
                            0.001f,
                            moveParams.ArriveRadius);

                    targetPos =
                        selfTransform.Position -
                        directionToTarget *
                        retreatDistance;

                    stopDistance =
                        math.max(
                            0.001f,
                            moveParams.ArriveRadius);

                    return;
                }

                targetPos =
                    selfTransform.Position;

                stopDistance =
                    math.max(
                        0.001f,
                        moveParams.ArriveRadius);
            }

            private static void BuildHoldPositionCombatTarget(
                in LocalTransform selfTransform,
                float3 targetCenter,
                in UnitMoveParams moveParams,
                out float3 targetPos,
                out float3 faceTargetPos,
                out float stopDistance,
                out float stopBandWidth)
            {
                targetPos =
                    selfTransform.Position;

                faceTargetPos =
                    targetCenter;

                stopDistance =
                    math.max(
                        0.001f,
                        moveParams.ArriveRadius);

                stopBandWidth =
                    0f;
            }

            private static void GetPreferredRangeBounds(
                in UnitMoveParams moveParams,
                in UnitAttackParams attackParams,
                in UnitAttackMovePolicy attackMovePolicy,
                out float minRange,
                out float maxRange)
            {
                minRange =
                    math.max(
                        0f,
                        attackMovePolicy.MinRange);

                float fallbackMax =
                    math.max(
                        moveParams.ArriveRadius,
                        attackParams.AttackRange);

                maxRange =
                    attackMovePolicy.MaxRange > 0f
                        ? attackMovePolicy.MaxRange
                        : fallbackMax;

                maxRange =
                    math.max(
                        minRange,
                        maxRange);

                float preferredRange =
                    math.clamp(
                        math.max(
                            0f,
                            attackMovePolicy.PreferredRange),
                        minRange,
                        maxRange);

                if (maxRange <= 0.0001f)
                {
                    maxRange =
                        math.max(
                            0.001f,
                            fallbackMax);
                }

                if (minRange > maxRange)
                {
                    minRange =
                        maxRange;
                }

                if (preferredRange <= 0.0001f && maxRange <= 0.0001f)
                {
                    maxRange =
                        math.max(
                            0.001f,
                            moveParams.ArriveRadius);
                }
            }

            private static float GetFootprintSupportRadius(
                float3 worldDir,
                quaternion rotation,
                in SwarmParams swarmParams)
            {
                GetFormationHalfExtents(
                    in swarmParams,
                    out float halfWidth,
                    out float halfDepth);

                float3 dir =
                    math.normalizesafe(
                        worldDir,
                        new float3(0f, 0f, 1f));

                dir.y = 0f;

                float3 right =
                    math.rotate(
                        rotation,
                        new float3(1f, 0f, 0f));

                float3 forward =
                    math.rotate(
                        rotation,
                        new float3(0f, 0f, 1f));

                right.y = 0f;
                forward.y = 0f;

                right =
                    math.normalizesafe(
                        right,
                        new float3(1f, 0f, 0f));

                forward =
                    math.normalizesafe(
                        forward,
                        new float3(0f, 0f, 1f));

                return
                    math.abs(math.dot(dir, right)) * halfWidth +
                    math.abs(math.dot(dir, forward)) * halfDepth;
            }

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

                halfWidth += padding;
                halfDepth += padding;

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

            // ======================================================
            //  State Helpers
            // ======================================================

            private static void SetIdle(
                ref UnitVelocity vel,
                ref UnitFsm fsm,
                ref UnitLocomotion locomotion,
                ref UnitAnimationReference animationRef,
                ref FormationSteering steering,
                ref FormationFrame formationFrame,
                in UnitAnimationReferenceCalibration calibration,
                float dt)
            {
                vel.Value =
                    math.lerp(
                        vel.Value,
                        float3.zero,
                        math.saturate(dt * 5f));

                steering.DesiredSpeed = 0f;
                steering.AngularVelocity = 0f;
                steering.DesiredDirection = float3.zero;

                fsm.OriMode =
                    OrientationMode.HoldLastFacing;

                fsm.FormMode =
                    FormationMode.Marching;

                fsm.WheelingWeight =
                    math.lerp(
                        fsm.WheelingWeight,
                        0f,
                        math.saturate(dt * 3f));

                fsm.State =
                    UnitState.Idle;

                locomotion.State =
                    LocomotionState.Idle;

                locomotion.NormalizedSpeed =
                    0f;

                formationFrame.TargetYaw =
                    formationFrame.CurrentYaw;

                formationFrame.TurnStartYaw =
                    formationFrame.CurrentYaw;

                formationFrame.TurnProgress =
                    0f;

                formationFrame.TurnMode =
                    FormationTurnMode.None;

                SetAnimationReferenceIdle(
                    ref animationRef,
                    in calibration);
            }

            private static void StopAndFaceTarget(
                ref LocalTransform lt,
                ref UnitVelocity vel,
                ref UnitFsm fsm,
                ref UnitLocomotion locomotion,
                ref UnitAnimationReference animationRef,
                ref FormationSteering steering,
                ref FormationFrame formationFrame,
                in UnitAnimationReferenceCalibration calibration,
                in UnitMoveParams moveParams,
                in FormationTurnParams turnParams,
                float3 faceTargetPos,
                float dt,
                float mapWidth,
                float mapHeight)
            {
                vel.Value =
                    math.lerp(
                        vel.Value,
                        float3.zero,
                        math.saturate(dt * 10f));

                float3 lookDir =
                    GetTorusDelta(
                        faceTargetPos,
                        lt.Position,
                        mapWidth,
                        mapHeight);

                lookDir.y = 0f;

                lookDir =
                    math.normalizesafe(
                        lookDir,
                        math.forward(lt.Rotation));

                if (math.lengthsq(lookDir) > 0.0001f)
                {
                    UpdateFormationTurnFrame(
                        ref formationFrame,
                        in turnParams,
                        lookDir,
                        lt.Rotation,
                        dt);

                    float3 oldForward =
                        math.normalizesafe(
                            math.forward(lt.Rotation),
                            new float3(0f, 0f, 1f));

                    quaternion targetRot =
                        quaternion.LookRotationSafe(
                            lookDir,
                            math.up());

                    lt.Rotation =
                        math.slerp(
                            lt.Rotation,
                            targetRot,
                            math.saturate(dt * moveParams.TurnRate));

                    float3 newForward =
                        math.normalizesafe(
                            math.forward(lt.Rotation),
                            new float3(0f, 0f, 1f));

                    steering.CurrentForward =
                        newForward;

                    float angleDelta =
                        math.acos(
                            math.clamp(
                                math.dot(oldForward, newForward),
                                -1f,
                                1f));

                    steering.AngularVelocity =
                        angleDelta /
                        math.max(dt, 0.0001f);

                    float crossY =
                        math.cross(
                            oldForward,
                            newForward).y;

                    steering.AngularVelocity *=
                        math.sign(crossY);
                }
                else
                {
                    steering.AngularVelocity = 0f;
                }

                steering.DesiredDirection =
                    lookDir;

                steering.DesiredSpeed =
                    0f;

                fsm.OriMode =
                    OrientationMode.FaceTarget;

                UpdateFormationMode(
                    ref fsm,
                    in formationFrame,
                    steering.AngularVelocity,
                    dt);

                fsm.State =
                    UnitState.Engaging;

                locomotion.State =
                    LocomotionState.Idle;

                locomotion.NormalizedSpeed =
                    0f;

                SetAnimationReferenceIdle(
                    ref animationRef,
                    in calibration);
            }

            // ======================================================
            //  Math Helpers
            // ======================================================

            private static float3 GetTorusDelta(
                float3 targetPos,
                float3 currentPos,
                float mapWidth,
                float mapHeight)
            {
                float3 delta =
                    targetPos -
                    currentPos;

                if (math.abs(delta.x) > mapWidth * 0.5f)
                {
                    delta.x -=
                        math.sign(delta.x) *
                        mapWidth;
                }

                if (math.abs(delta.z) > mapHeight * 0.5f)
                {
                    delta.z -=
                        math.sign(delta.z) *
                        mapHeight;
                }

                return delta;
            }

            private static float GetYawFromRotation(
                quaternion rotation)
            {
                float3 forward =
                    math.normalizesafe(
                        math.forward(rotation),
                        new float3(0f, 0f, 1f));

                return GetYawFromDirection(forward);
            }

            private static float GetYawFromDirection(
                float3 direction)
            {
                float3 dir =
                    math.normalizesafe(
                        direction,
                        new float3(0f, 0f, 1f));

                dir.y = 0f;

                dir =
                    math.normalizesafe(
                        dir,
                        new float3(0f, 0f, 1f));

                return
                    math.atan2(
                        dir.x,
                        dir.z);
            }

            private static float DeltaAngleRadians(
                float from,
                float to)
            {
                return
                    math.atan2(
                        math.sin(to - from),
                        math.cos(to - from));
            }

            private static float MoveTowardsAngleRadians(
                float current,
                float target,
                float maxDelta)
            {
                float delta =
                    DeltaAngleRadians(
                        current,
                        target);

                if (math.abs(delta) <= maxDelta)
                {
                    return target;
                }

                return
                    current +
                    math.sign(delta) *
                    maxDelta;
            }

            private static float GetUnsignedAngleRadians(
                float3 from,
                float3 to)
            {
                float3 a =
                    math.normalizesafe(
                        from,
                        new float3(0f, 0f, 1f));

                float3 b =
                    math.normalizesafe(
                        to,
                        new float3(0f, 0f, 1f));

                a.y = 0f;
                b.y = 0f;

                a =
                    math.normalizesafe(
                        a,
                        new float3(0f, 0f, 1f));

                b =
                    math.normalizesafe(
                        b,
                        new float3(0f, 0f, 1f));

                return
                    math.acos(
                        math.clamp(
                            math.dot(a, b),
                            -1f,
                            1f));
            }

            private static float GetCommandWalkSpeed(
                in UnitMoveParams moveParams)
            {
                float walkNormalized =
                    math.max(
                        0.05f,
                        moveParams.RunThresholdNormalized * 0.8f);

                walkNormalized =
                    math.min(
                        walkNormalized,
                        math.max(
                            0.05f,
                            moveParams.RunThresholdNormalized - 0.01f));

                return
                    moveParams.MaxSpeed *
                    walkNormalized;
            }

            private static float GetTurnSpeedMultiplier(
                float angleToDesiredRad)
            {
                float angleDeg =
                    math.degrees(
                        angleToDesiredRad);

                float turnT =
                    math.saturate(
                        (angleDeg - 30f) / 90f);

                return
                    math.lerp(
                        1.0f,
                        0.25f,
                        turnT);
            }

            // ======================================================
            //  Animation Reference Helpers
            // ======================================================

            private static void SetAnimationReferenceIdle(
                ref UnitAnimationReference animationRef,
                in UnitAnimationReferenceCalibration calibration)
            {
                animationRef.Speed = 0f;
                animationRef.MotionSpeed = 0f;
                animationRef.Grounded =
                    calibration.IdleGrounded;
            }

            private static void UpdateAnimationReference(
                ref UnitAnimationReference animationRef,
                in UnitLocomotion locomotion,
                in UnitMoveParams moveParams,
                in UnitAnimationReferenceCalibration calibration)
            {
                animationRef.Grounded = true;

                if (locomotion.State == LocomotionState.Idle)
                {
                    animationRef.Speed = 0f;
                    animationRef.MotionSpeed = 0f;
                    animationRef.Grounded =
                        calibration.IdleGrounded;

                    return;
                }

                float normalizedSpeed =
                    math.saturate(
                        locomotion.NormalizedSpeed);

                float runThreshold =
                    math.max(
                        moveParams.RunThresholdNormalized,
                        0.0001f);

                if (locomotion.State == LocomotionState.Walk)
                {
                    float walkT =
                        math.saturate(
                            normalizedSpeed /
                            runThreshold);

                    animationRef.Speed =
                        math.lerp(
                            calibration.WalkSpeedMin,
                            calibration.WalkSpeedMax,
                            walkT);

                    animationRef.MotionSpeed =
                        math.lerp(
                            calibration.WalkMotionMin,
                            calibration.WalkMotionMax,
                            walkT);

                    return;
                }

                float runRange =
                    math.max(
                        1f - moveParams.RunThresholdNormalized,
                        0.0001f);

                float runT =
                    math.saturate(
                        (normalizedSpeed - moveParams.RunThresholdNormalized) /
                        runRange);

                animationRef.Speed =
                    math.lerp(
                        calibration.RunSpeedMin,
                        calibration.RunSpeedMax,
                        runT);

                animationRef.MotionSpeed =
                    math.lerp(
                        calibration.RunMotionMin,
                        calibration.RunMotionMax,
                        runT);
            }
        }
    }
}
