using Detection;
using Swarm;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

public class UnitAuthoring : MonoBehaviour
{
    [Header("Identity & Render")]
    [SerializeField] private uint unitId = 1;
    [SerializeField] private byte materialIndex = 0;
    [SerializeField] private byte meshIndex = 0;

    [Header("Move Params (Unit Base)")]
    [Min(0f)][SerializeField] private float maxSpeed = 5f;
    [Min(0f)][SerializeField] private float accel = 25f;
    [Min(0f)][SerializeField] private float turnRate = 5f;
    [Min(0.001f)][SerializeField] private float arriveRadius = 0.5f;
    [Min(0f)][SerializeField] private float idleSpeedEpsilon = 0.05f;
    [Range(0f, 1f)][SerializeField] private float runThresholdNormalized = 0.85f;

    [Header("Formation Turn Params")]
    [Tooltip("이 각도 이상이면 일반 행군이 아니라 큰 방향 전환으로 봅니다.")]
    [Range(0f, 180f)]
    [SerializeField] private float wheelingAngleThresholdDegrees = 60f;

    [Tooltip("이 각도 이상이면 AboutFace로 봅니다. 90도 이상부터 뒤돌기를 원하면 90으로 설정하십시오.")]
    [Range(0f, 180f)]
    [SerializeField] private float aboutFaceAngleThresholdDegrees = 100f;

    [Tooltip("대열 기준 Yaw가 목표 Yaw를 따라가는 속도입니다.")]
    [Min(0f)]
    [SerializeField] private float formationYawTurnSpeed = 8f;

    [Tooltip("병사 개별 FacingYaw가 목표 방향으로 회전하는 속도입니다.")]
    [Min(0f)]
    [SerializeField] private float soldierFacingTurnSpeed = 14f;

    [Tooltip("큰 방향 전환 중 유닛 루트 이동 속도 배율입니다.")]
    [Range(0f, 1f)]
    [SerializeField] private float largeTurnMoveSpeedScale = 0.35f;

    [Tooltip("AboutFace 중 유닛 루트 이동 속도 배율입니다. 보통 0~0.15 권장.")]
    [Range(0f, 1f)]
    [SerializeField] private float aboutFaceMoveSpeedScale = 0.05f;

    [Tooltip("큰 방향 전환 중 병사가 새 슬롯을 추적하는 강도입니다. 낮을수록 현재 위치를 유지합니다.")]
    [Range(0f, 1f)]
    [SerializeField] private float slotFollowScaleDuringTurn = 0.15f;

    [Tooltip("큰 방향 전환 중 내부 병사 분리 강도 배율입니다.")]
    [Min(0f)]
    [SerializeField] private float separationScaleDuringTurn = 2.0f;

    [Tooltip("큰 방향 전환 중 개인 공간 배율입니다.")]
    [Min(0f)]
    [SerializeField] private float personalSpaceScaleDuringTurn = 1.1f;

    [Tooltip("큰 방향 전환 중 위치 보정 Projection 강도입니다.")]
    [Range(0f, 1f)]
    [SerializeField] private float positionProjectionStrength = 0.65f;

    [Tooltip("큰 방향 전환 중 위치 보정 Projection 반복 횟수입니다.")]
    [Range(0, 4)]
    [SerializeField] private int positionProjectionIterations = 2;

    [Tooltip("목표 방향과 이 각도 이하로 가까워지면 방향 전환 완료로 봅니다.")]
    [Range(0f, 45f)]
    [SerializeField] private float turnCompleteAngleDegrees = 5f;

    [Header("Attack Params")]
    [Min(0f)][SerializeField] private float attackRange = 4.0f;
    [Min(0f)][SerializeField] private float attackCooldown = 1.4f;

    [Header("Attack Move Policy Override")]
    [SerializeField] private bool overrideAttackMovePolicy = false;
    [SerializeField] private AttackMovementPolicy attackMovementPolicy = AttackMovementPolicy.Contact;
    [Min(0f)][SerializeField] private float preferredAttackRange = 0f;
    [Min(0f)][SerializeField] private float minAttackRange = 0f;
    [Min(0f)][SerializeField] private float maxAttackRange = 0f;

    [Header("Contact Params (Unit Footprint)")]
    [Tooltip("적 유닛끼리 허용할 대형 겹침 비율입니다. 0.25면 약 25% 겹침을 허용합니다.")]
    [Range(0f, 0.75f)]
    [SerializeField] private float enemyAllowedOverlapRatio = 0.25f;

    [Tooltip("같은 팩션 유닛끼리 허용할 대형 겹침 비율입니다. 보통 적 유닛보다 낮게 둡니다.")]
    [Range(0f, 0.75f)]
    [SerializeField] private float sameFactionAllowedOverlapRatio = 0.05f;

    [Tooltip("접촉 거리 주변에서 정지 상태로 인정할 폭입니다. 너무 작으면 전투선에서 진동할 수 있습니다.")]
    [Min(0f)]
    [SerializeField] private float contactBandWidth = 0.75f;

    [Tooltip("Footprint 계산 후 추가로 둘 여유 거리입니다. 0이면 실제 대형 크기 기준입니다.")]
    [Min(0f)]
    [SerializeField] private float contactPadding = 0.0f;

    [Tooltip("적 유닛과 과하게 겹쳤을 때의 보정 강도입니다.")]
    [Min(0f)]
    [SerializeField] private float enemyCorrectionStrength = 0.25f;

    [Tooltip("같은 팩션 유닛과 겹쳤을 때의 보정 강도입니다.")]
    [Min(0f)]
    [SerializeField] private float sameFactionCorrectionStrength = 0.75f;

    [Tooltip("전투 상태에서 회피 보정을 얼마나 약화할지 결정합니다. 0이면 전투 중 회피 없음, 1이면 일반 회피와 동일합니다.")]
    [Range(0f, 1f)]
    [SerializeField] private float engagingAvoidanceScale = 0.25f;

    [Tooltip("한 tick에서 허용되는 최대 위치 보정 거리입니다.")]
    [Min(0f)]
    [SerializeField] private float maxContactCorrectionPerTick = 0.45f;

    [Tooltip("서로 파고드는 방향의 속도를 얼마나 감쇠할지 결정합니다.")]
    [Range(0f, 1f)]
    [SerializeField] private float contactVelocityDamping = 0.35f;

    [Header("Swarm Formation (직사각형 대열)")]
    [Min(1)][SerializeField] private int soldierCount = 256;
    [Min(1)][SerializeField] private int columns = 16;
    [SerializeField] private float spacingX = 1.0f;
    [SerializeField] private float spacingZ = 1.0f;
    [SerializeField] private float baseJitter = 0.15f;
    [SerializeField] private uint seed = 12345;

    [Header("Swarm Crowd (인파 및 디싱크)")]
    [SerializeField] private float maxLocalSpeed = 6f;
    [SerializeField] private float followGain = 5f;
    [SerializeField] private float separationWeight = 2.5f;
    [SerializeField] private float personalSpace = 0.8f;

    [Header("Swarm Engagement (전투 시 병사 이탈)")]
    [Tooltip("전투 상태에서 병사 인스턴스가 기본 슬롯에서 전방으로 이동할 수 있는 최대 거리입니다.")]
    [Min(0f)]
    [SerializeField] private float maxEngageAdvance = 0.8f;

    [Tooltip("전열 병사의 전진 가중치입니다. slot.Frontness와 함께 사용됩니다.")]
    [Min(0f)]
    [SerializeField] private float frontAdvanceScale = 1.0f;

    [Tooltip("후열 병사의 최소 전진 가중치입니다. 0이면 후열은 거의 움직이지 않습니다.")]
    [Range(0f, 1f)]
    [SerializeField] private float rearAdvanceScale = 0.15f;

    [Tooltip("전투 중 좌우 흐트러짐 허용 거리입니다. 너무 크면 대형이 무너집니다.")]
    [Min(0f)]
    [SerializeField] private float lateralFreedom = 0.25f;

    [Tooltip("상대 유닛 Footprint 경계에서 약간 바깥쪽을 목표로 삼기 위한 여유 거리입니다.")]
    [Min(0f)]
    [SerializeField] private float targetFootprintPadding = 0.2f;

    [Tooltip("전투 전진 목표로 끌어당기는 강도입니다.")]
    [Min(0f)]
    [SerializeField] private float engageMoveStrength = 1.0f;

    [Tooltip("전투 중에도 원래 슬롯을 유지하려는 가중치입니다. 높을수록 직사각형 대형이 강하게 유지됩니다.")]
    [Range(0f, 1f)]
    [SerializeField] private float slotHoldStrength = 0.65f;

    [Tooltip("전투 중 병사 FacingYaw가 목표 방향으로 반응하는 강도입니다.")]
    [Min(0f)]
    [SerializeField] private float facingSharpness = 12f;

    [Header("Swarm State Tuning (상태별 응집력)")]
    [Range(0f, 10f)][SerializeField] private float idleTightness = 8f;
    [Range(0f, 10f)][SerializeField] private float movingTightness = 5f;
    [Range(0f, 10f)][SerializeField] private float engagingTightness = 1f;
    [Range(0f, 10f)][SerializeField] private float retreatingTightness = 0.5f;

    [Header("Swarm Animation Approximation Tuning")]
    [Min(0f)][SerializeField] private float idleVelocityScale = 1f;
    [Min(0f)][SerializeField] private float walkVelocityScale = 1f;
    [Min(0f)][SerializeField] private float runVelocityScale = 1f;

    [Min(0f)][SerializeField] private float idleSettleScale = 1f;
    [Min(0f)][SerializeField] private float walkSettleScale = 1f;
    [Min(0f)][SerializeField] private float runSettleScale = 1f;

    [Min(0f)][SerializeField] private float idlePhaseScale = 1f;
    [Min(0f)][SerializeField] private float walkPhaseScale = 1f;
    [Min(0f)][SerializeField] private float runPhaseScale = 1f;

    [Min(0f)][SerializeField] private float idleSwayScale = 1f;
    [Min(0f)][SerializeField] private float walkSwayScale = 1f;
    [Min(0f)][SerializeField] private float runSwayScale = 1f;

    [Min(0f)][SerializeField] private float idleRenderScale = 1f;
    [Min(0f)][SerializeField] private float walkRenderScale = 1f;
    [Min(0f)][SerializeField] private float runRenderScale = 1f;

    [Min(0f)][SerializeField] private float visualWalkSpeedThreshold = 0.08f;
    [Min(0f)][SerializeField] private float visualRunSpeedThreshold = 2.4f;
    [Min(0.001f)][SerializeField] private float visualMotionSpeedDivisor = 3.5f;

    [Header("Swarm Animation Desync")]
    [Min(0f)][SerializeField] private float oneShotDelayMin = 0f;
    [Min(0f)][SerializeField] private float oneShotDelayMax = 0.25f;

    [Header("Validator Animation Reference Calibration")]
    [SerializeField] private bool idleGrounded = true;

    [Min(0f)][SerializeField] private float walkReferenceSpeedMin = 0.6f;
    [Min(0f)][SerializeField] private float walkReferenceSpeedMax = 2f;
    [Range(0f, 1f)][SerializeField] private float walkReferenceMotionMin = 0.35f;
    [Range(0f, 1f)][SerializeField] private float walkReferenceMotionMax = 0.6f;

    [Min(0f)][SerializeField] private float runReferenceSpeedMin = 2f;
    [Min(0f)][SerializeField] private float runReferenceSpeedMax = 5.335f;
    [Range(0f, 1f)][SerializeField] private float runReferenceMotionMin = 0.6f;
    [Range(0f, 1f)][SerializeField] private float runReferenceMotionMax = 1f;

    [Header("Detection Params")]
    [SerializeField] private int factionId = 0;
    [SerializeField] private bool isBase = false;
    [SerializeField] private float searchRange = 20f;
    [SerializeField] private float tauntWeight = 1f;
    [SerializeField] private float targetTauntThreshold = 5f;

    class Baker : Baker<UnitAuthoring>
    {
        public override void Bake(UnitAuthoring authoring)
        {
            var entity = GetEntity(TransformUsageFlags.Dynamic | TransformUsageFlags.Renderable);

            RangedUnitAuthoring rangedAuthoring =
                authoring.GetComponent<RangedUnitAuthoring>();

            bool isRangedUnit =
                rangedAuthoring != null;

            // ==================================================
            // [1] Core Unit Components
            // ==================================================

            AddComponent<UnitTag>(entity);

            AddComponent(entity, new UnitId
            {
                Value = authoring.unitId
            });

            AddComponent(entity, new UnitRoleComponent
            {
                Value = isRangedUnit ? UnitRole.Archer : UnitRole.MeleeInfantry
            });

            AddComponent(entity, new UnitFsm
            {
                State = UnitState.Idle,
                OriMode = OrientationMode.HoldLastFacing,
                FormMode = FormationMode.Marching,
                WheelingWeight = 0f
            });

            AddComponent(entity, new UnitLocomotion
            {
                State = LocomotionState.Idle,
                NormalizedSpeed = 0f
            });

            AddComponent(entity, new UnitAnimationReference
            {
                Speed = 0f,
                MotionSpeed = 0f,
                Grounded = true
            });

            AddComponent(entity, new UnitAnimationState
            {
                Clip = UnitAnimationClipId.Idle,
                PreviousClip = UnitAnimationClipId.Idle,
                Mode = UnitAnimationMode.Loop,
                NormalizedTime = 0f,
                PlaybackSpeed = 1f,
                BlendTime = 0f,
                LockRemainingTime = 0f,
                Priority = 0
            });

            AddComponent(entity, new UnitAnimationRequest
            {
                HasRequest = false,
                Clip = UnitAnimationClipId.Idle,
                Mode = UnitAnimationMode.Loop,
                Duration = 0f,
                PlaybackSpeed = 1f,
                Priority = 0,
                ClearAfterApply = true
            });

            UnitAnimationProfile animationProfile = isRangedUnit
                ? rangedAuthoring.BuildAnimationProfile()
                : new UnitAnimationProfile
                {
                    IdleClip = UnitAnimationClipId.Idle,
                    WalkClip = UnitAnimationClipId.Walk,
                    RunClip = UnitAnimationClipId.Run,
                    PrimaryAttackClip = UnitAnimationClipId.Fight
                };

            AddComponent(entity, animationProfile);

            AddComponent(entity, new UnitAnimationReferenceCalibration
            {
                IdleGrounded = authoring.idleGrounded,

                WalkSpeedMin = authoring.walkReferenceSpeedMin,
                WalkSpeedMax = authoring.walkReferenceSpeedMax,
                WalkMotionMin = authoring.walkReferenceMotionMin,
                WalkMotionMax = authoring.walkReferenceMotionMax,

                RunSpeedMin = authoring.runReferenceSpeedMin,
                RunSpeedMax = authoring.runReferenceSpeedMax,
                RunMotionMin = authoring.runReferenceMotionMin,
                RunMotionMax = authoring.runReferenceMotionMax
            });

            AddComponent(entity, new UnitRenderVariant
            {
                MaterialIndex = authoring.materialIndex,
                MeshIndex = authoring.meshIndex
            });

            AddComponent(entity, new UnitMoveParams
            {
                MaxSpeed = math.max(0f, authoring.maxSpeed),
                Accel = math.max(0f, authoring.accel),
                TurnRate = math.max(0f, authoring.turnRate),
                ArriveRadius = math.max(0.001f, authoring.arriveRadius),
                IdleSpeedEpsilon = math.max(0f, authoring.idleSpeedEpsilon),
                RunThresholdNormalized = math.saturate(authoring.runThresholdNormalized)
            });

            float wheelingAngleThreshold =
                math.clamp(authoring.wheelingAngleThresholdDegrees, 0f, 180f);

            float aboutFaceAngleThreshold =
                math.clamp(authoring.aboutFaceAngleThresholdDegrees, 0f, 180f);

            aboutFaceAngleThreshold =
                math.max(wheelingAngleThreshold, aboutFaceAngleThreshold);

            AddComponent(entity, new FormationTurnParams
            {
                WheelingAngleThresholdDegrees = wheelingAngleThreshold,
                AboutFaceAngleThresholdDegrees = aboutFaceAngleThreshold,

                FormationYawTurnSpeed = math.max(0f, authoring.formationYawTurnSpeed),
                SoldierFacingTurnSpeed = math.max(0f, authoring.soldierFacingTurnSpeed),

                LargeTurnMoveSpeedScale = math.saturate(authoring.largeTurnMoveSpeedScale),
                AboutFaceMoveSpeedScale = math.saturate(authoring.aboutFaceMoveSpeedScale),

                SlotFollowScaleDuringTurn = math.saturate(authoring.slotFollowScaleDuringTurn),
                SeparationScaleDuringTurn = math.max(0f, authoring.separationScaleDuringTurn),
                PersonalSpaceScaleDuringTurn = math.max(0f, authoring.personalSpaceScaleDuringTurn),

                PositionProjectionStrength = math.saturate(authoring.positionProjectionStrength),
                PositionProjectionIterations = math.max(0, authoring.positionProjectionIterations),

                TurnCompleteAngleDegrees = math.clamp(authoring.turnCompleteAngleDegrees, 0f, 45f)
            });

            float defaultAttackRange =
                math.max(0f, authoring.attackRange);

            AddComponent(entity, new UnitAttackParams
            {
                AttackRange = defaultAttackRange,
                AttackCooldown = math.max(0f, authoring.attackCooldown)
            });

            UnitAttackMovePolicy attackMovePolicy = isRangedUnit
                ? rangedAuthoring.BuildAttackMovePolicy()
                : BuildDefaultAttackMovePolicy(authoring, defaultAttackRange);

            AddComponent(entity, attackMovePolicy);

            AddComponent(entity, new UnitContactParams
            {
                EnemyAllowedOverlapRatio = math.saturate(authoring.enemyAllowedOverlapRatio),
                SameFactionAllowedOverlapRatio = math.saturate(authoring.sameFactionAllowedOverlapRatio),
                ContactBandWidth = math.max(0f, authoring.contactBandWidth),
                ContactPadding = math.max(0f, authoring.contactPadding),
                EnemyCorrectionStrength = math.max(0f, authoring.enemyCorrectionStrength),
                SameFactionCorrectionStrength = math.max(0f, authoring.sameFactionCorrectionStrength),
                EngagingAvoidanceScale = math.saturate(authoring.engagingAvoidanceScale),
                MaxCorrectionPerTick = math.max(0f, authoring.maxContactCorrectionPerTick),
                VelocityDamping = math.saturate(authoring.contactVelocityDamping)
            });

            AddComponent(entity, new UnitVelocity
            {
                Value = float3.zero
            });

            AddComponent(entity, new UnitPreviousPos
            {
                Value = authoring.transform.position
            });

            AddComponent(entity, new UnitPreviousRot
            {
                Value = authoring.transform.rotation
            });

            AddComponent(entity, new UnitMoveTarget
            {
                HasTarget = false,
                Position = float3.zero
            });

            AddComponent(entity, new FormationSteering
            {
                DesiredDirection = float3.zero,
                DesiredSpeed = 0f,
                CurrentForward = math.forward(authoring.transform.rotation),
                AngularVelocity = 0f
            });

            float3 initialForward = math.forward(authoring.transform.rotation);
            float initialYaw = math.atan2(initialForward.x, initialForward.z);

            AddComponent(entity, new FormationFrame
            {
                CurrentYaw = initialYaw,
                TargetYaw = initialYaw,
                TurnStartYaw = initialYaw,
                TurnProgress = 0f,
                SlotParity = 1,
                TurnMode = FormationTurnMode.None
            });

            AddBuffer<UnitCommand>(entity);

            AddComponent(entity, new UnitCommandCursor
            {
                LastAppliedTick = 0
            });

            // ==================================================
            // [2] Swarm Params
            // ==================================================

            AddComponent(entity, new SwarmParams
            {
                SoldierCount = math.max(1, authoring.soldierCount),
                Columns = math.max(1, authoring.columns),
                SpacingX = authoring.spacingX,
                SpacingZ = authoring.spacingZ,
                BaseJitter = math.max(0f, authoring.baseJitter),
                Seed = authoring.seed
            });

            AddComponent(entity, new SwarmCrowdParams
            {
                MaxLocalSpeed = math.max(0f, authoring.maxLocalSpeed),
                FollowGain = math.max(0f, authoring.followGain),
                SeparationWeight = math.max(0f, authoring.separationWeight),
                PersonalSpace = math.max(0f, authoring.personalSpace)
            });

            AddComponent(entity, new SwarmEngagementParams
            {
                MaxEngageAdvance = math.max(0f, authoring.maxEngageAdvance),
                FrontAdvanceScale = math.max(0f, authoring.frontAdvanceScale),
                RearAdvanceScale = math.saturate(authoring.rearAdvanceScale),
                LateralFreedom = math.max(0f, authoring.lateralFreedom),
                TargetFootprintPadding = math.max(0f, authoring.targetFootprintPadding),
                EngageMoveStrength = math.max(0f, authoring.engageMoveStrength),
                SlotHoldStrength = math.saturate(authoring.slotHoldStrength),
                FacingSharpness = math.max(0f, authoring.facingSharpness)
            });

            AddComponent(entity, new SwarmStateTuning
            {
                IdleTightness = math.max(0f, authoring.idleTightness),
                MovingTightness = math.max(0f, authoring.movingTightness),
                EngagingTightness = math.max(0f, authoring.engagingTightness),
                RetreatingTightness = math.max(0f, authoring.retreatingTightness)
            });

            AddComponent(entity, new SwarmAnimationApproximationTuning
            {
                IdleVelocityScale = math.max(0f, authoring.idleVelocityScale),
                WalkVelocityScale = math.max(0f, authoring.walkVelocityScale),
                RunVelocityScale = math.max(0f, authoring.runVelocityScale),

                IdleSettleScale = math.max(0f, authoring.idleSettleScale),
                WalkSettleScale = math.max(0f, authoring.walkSettleScale),
                RunSettleScale = math.max(0f, authoring.runSettleScale),

                IdlePhaseScale = math.max(0f, authoring.idlePhaseScale),
                WalkPhaseScale = math.max(0f, authoring.walkPhaseScale),
                RunPhaseScale = math.max(0f, authoring.runPhaseScale),

                IdleSwayScale = math.max(0f, authoring.idleSwayScale),
                WalkSwayScale = math.max(0f, authoring.walkSwayScale),
                RunSwayScale = math.max(0f, authoring.runSwayScale),

                IdleRenderScale = math.max(0f, authoring.idleRenderScale),
                WalkRenderScale = math.max(0f, authoring.walkRenderScale),
                RunRenderScale = math.max(0f, authoring.runRenderScale),

                VisualWalkSpeedThreshold = math.max(0f, authoring.visualWalkSpeedThreshold),
                VisualRunSpeedThreshold = math.max(0f, authoring.visualRunSpeedThreshold),
                VisualMotionSpeedDivisor = math.max(0.001f, authoring.visualMotionSpeedDivisor)
            });

            float oneShotDelayMin = math.max(0f, authoring.oneShotDelayMin);
            float oneShotDelayMax = math.max(oneShotDelayMin, authoring.oneShotDelayMax);

            AddComponent(entity, new SwarmAnimationDesyncTuning
            {
                OneShotDelayMin = oneShotDelayMin,
                OneShotDelayMax = oneShotDelayMax
            });

            AddComponent(entity, new SwarmRuntime
            {
                GeneratedCount = 0,
                GeneratedColumns = 0,
                GeneratedSpacingX = 0f,
                GeneratedSpacingZ = 0f,
                GeneratedSeed = 0
            });

            // ==================================================
            // [3] Swarm Buffers
            // ==================================================

            AddBuffer<SoldierSlot>(entity);
            AddBuffer<SoldierAgent>(entity);
            AddBuffer<SoldierVariance>(entity);
            AddBuffer<SoldierCombatState>(entity);

            // ==================================================
            // [4] Detection Components
            // ==================================================

            AddComponent(entity, new DetectionTag
            {
                FactionId = authoring.factionId,
                IsBase = authoring.isBase,
                SearchRange = math.max(0f, authoring.searchRange),
                TauntWeight = authoring.tauntWeight,
                TargetTauntThreshold = authoring.targetTauntThreshold
            });

            AddComponent(entity, new DetectionTarget
            {
                HasTarget = false,
                CenterTarget = Entity.Null,
                TargetPos = float3.zero
            });

            // Snapshot capture is an optional debug/replay path.
            // Keeping it off the baked unit archetype leaves room for
            // SubScene's LinkedEntityGroup metadata.
        }

        private static UnitAttackMovePolicy BuildDefaultAttackMovePolicy(
            UnitAuthoring authoring,
            float defaultAttackRange)
        {
            if (!authoring.overrideAttackMovePolicy)
            {
                return new UnitAttackMovePolicy
                {
                    Policy = AttackMovementPolicy.Contact,
                    PreferredRange = 0f,
                    MinRange = 0f,
                    MaxRange = defaultAttackRange
                };
            }

            float minRange =
                math.max(
                    0f,
                    authoring.minAttackRange);

            float maxRange =
                authoring.maxAttackRange > 0f
                    ? authoring.maxAttackRange
                    : defaultAttackRange;

            maxRange =
                math.max(
                    minRange,
                    maxRange);

            float preferredRange =
                math.clamp(
                    math.max(
                        0f,
                        authoring.preferredAttackRange),
                    minRange,
                    maxRange);

            return new UnitAttackMovePolicy
            {
                Policy = authoring.attackMovementPolicy,
                PreferredRange = preferredRange,
                MinRange = minRange,
                MaxRange = maxRange
            };
        }
    }
}
