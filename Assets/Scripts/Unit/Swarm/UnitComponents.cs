using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Swarm
{
    // ==========================================================
    //  Core Unit
    // ==========================================================

    /// <summary>
    /// Swarm Unit임을 표시하는 태그 컴포넌트.
    /// </summary>
    public struct UnitTag : IComponentData { }

    /// <summary>
    /// Unit 식별자.
    /// </summary>
    public struct UnitId : IComponentData
    {
        public uint Value;
    }

    /// <summary>
    /// Unit의 큰 역할 구분.
    /// 디버그, UI, 프리팹 식별 용도이며 실제 전투/이동 시스템 분기는 기능 태그와 정책 컴포넌트를 우선 사용한다.
    /// </summary>
    public enum UnitRole : byte
    {
        MeleeInfantry = 0,
        Archer = 1
    }

    /// <summary>
    /// Unit 역할 식별 컴포넌트.
    /// 시스템 동작 분기보다는 디버그와 authoring 의도 표시용으로 사용한다.
    /// </summary>
    public struct UnitRoleComponent : IComponentData
    {
        public UnitRole Value;
    }

    /// <summary>
    /// Unit의 상위 FSM 상태.
    /// </summary>
    public enum UnitState : byte
    {
        Idle = 0,
        Moving = 1,
        Engaging = 2,
        Retreating = 3
    }

    /// <summary>
    /// 이동 애니메이션 판정을 위한 하위 이동 상태.
    /// </summary>
    public enum LocomotionState : byte
    {
        Idle = 0,
        Walk = 1,
        Run = 2
    }

    /// <summary>
    /// GPU 스키닝 또는 렌더링 payload에서 사용할 애니메이션 클립 ID.
    /// 현재는 Idle / Walk / Run / Fight 4종.
    /// </summary>
    public enum UnitAnimationClipId : byte
    {
        Idle = 0,
        Walk = 1,
        Run = 2,
        Fight = 3,
        BowShoot = 4
    }

    /// <summary>
    /// 애니메이션 재생 방식.
    /// Loop: 반복 재생
    /// OneShot: 1회 재생
    /// </summary>
    public enum UnitAnimationMode : byte
    {
        Loop = 0,
        OneShot = 1
    }

    /// <summary>
    /// Unit 본체의 방향 제어 방식.
    /// </summary>
    public enum OrientationMode : byte
    {
        FaceMovement,
        FaceTarget,
        HoldLastFacing
    }

    /// <summary>
    /// 대열 회전 방식.
    /// Marching: 일반 행군 대열
    /// Wheeling: 곡선 회전 대열
    /// </summary>
    public enum FormationMode : byte
    {
        Marching,
        Wheeling
    }

    /// <summary>
    /// 위치 보존형 방향 전환 상태.
    /// None: 일반 이동
    /// LeftFace: 좌향좌 계열 큰 방향 전환
    /// RightFace: 우향우 계열 큰 방향 전환
    /// AboutFace: 뒤로 돌아
    /// </summary>
    public enum FormationTurnMode : byte
    {
        None = 0,
        LeftFace = 1,
        RightFace = 2,
        AboutFace = 3
    }

    /// <summary>
    /// 대열 배치 기준 방향.
    ///
    /// 기존에는 Unit 본체 회전(LocalTransform.Rotation)이 곧 병사 슬롯 기준 방향이었다.
    /// 큰 방향 전환에서는 이 방식이 병사 슬롯 교차를 유발하므로,
    /// 유닛 본체 방향과 병사 대열 기준 방향을 분리한다.
    /// </summary>
    public struct FormationFrame : IComponentData
    {
        /// <summary>
        /// 현재 대열 슬롯 기준 Yaw. 라디안 단위.
        /// CrowdSimulationSystem은 병사 슬롯을 이 방향 기준으로 배치한다.
        /// </summary>
        public float CurrentYaw;

        /// <summary>
        /// 목표 대열 또는 목표 시선 Yaw. 라디안 단위.
        /// UnitSimulationSystem이 목표 이동 방향을 기준으로 갱신한다.
        /// </summary>
        public float TargetYaw;

        /// <summary>
        /// 방향 전환을 시작한 시점의 대열 Yaw.
        /// TurnProgress 계산, 보간, 디버그에 사용한다.
        /// </summary>
        public float TurnStartYaw;

        /// <summary>
        /// 현재 방향 전환 진행도.
        /// 0이면 시작, 1이면 완료.
        /// </summary>
        public float TurnProgress;

        /// <summary>
        /// 병사 슬롯의 앞뒤/좌우 의미를 보존하기 위한 패리티.
        ///
        /// +1: 원래 슬롯 사용
        /// -1: BaseLocalPos의 x,z를 반전해서 사용
        ///
        /// AboutFace 완료 시 SlotParity를 반전하면,
        /// 병사들이 서로 슬롯을 교환하지 않고 각자 제자리에서 뒤돌아선 것처럼 유지된다.
        /// </summary>
        public int SlotParity;

        /// <summary>
        /// 현재 방향 전환 모드.
        /// </summary>
        public FormationTurnMode TurnMode;
    }

    /// <summary>
    /// 대열 방향 전환 튜닝 파라미터.
    /// UnitSimulationSystem과 CrowdSimulationSystem에서 함께 사용한다.
    /// </summary>
    public struct FormationTurnParams : IComponentData
    {
        /// <summary>
        /// 이 각도 이상이면 일반 Marching이 아니라 큰 방향 전환으로 본다.
        /// 예: 60도.
        /// </summary>
        public float WheelingAngleThresholdDegrees;

        /// <summary>
        /// 이 각도 이상이면 AboutFace로 본다.
        /// 90도 이상에서 각자 뒤돌기를 원하면 90~100 사이로 둔다.
        /// </summary>
        public float AboutFaceAngleThresholdDegrees;

        /// <summary>
        /// FormationFrame.CurrentYaw가 TargetYaw를 따라가는 속도.
        /// 라디안/초 개념으로 사용한다.
        /// </summary>
        public float FormationYawTurnSpeed;

        /// <summary>
        /// 병사 개별 FacingYaw가 목표 방향으로 회전하는 속도.
        /// </summary>
        public float SoldierFacingTurnSpeed;

        /// <summary>
        /// 큰 방향 전환 중 유닛 루트 이동 속도 배율.
        /// 0이면 제자리 회전에 가깝고, 1이면 일반 이동과 동일하다.
        /// </summary>
        public float LargeTurnMoveSpeedScale;

        /// <summary>
        /// AboutFace 중 유닛 루트 이동 속도 배율.
        /// 보통 0~0.15 권장.
        /// </summary>
        public float AboutFaceMoveSpeedScale;

        /// <summary>
        /// 큰 방향 전환 중 병사가 새 슬롯을 추적하는 강도.
        /// 낮을수록 병사가 현재 위치를 유지하고, 높을수록 새 슬롯으로 이동한다.
        /// </summary>
        public float SlotFollowScaleDuringTurn;

        /// <summary>
        /// 큰 방향 전환 중 내부 병사 분리 강도 배율.
        /// </summary>
        public float SeparationScaleDuringTurn;

        /// <summary>
        /// 큰 방향 전환 중 개인 공간 배율.
        /// </summary>
        public float PersonalSpaceScaleDuringTurn;

        /// <summary>
        /// 위치 보정 Projection 강도.
        /// 0이면 보정 없음, 1이면 계산된 겹침 보정을 전부 적용.
        /// </summary>
        public float PositionProjectionStrength;

        /// <summary>
        /// 위치 보정 Projection 반복 횟수.
        /// 0이면 Projection 없음.
        /// </summary>
        public int PositionProjectionIterations;

        /// <summary>
        /// 이 각도 이하로 목표 방향과 가까워지면 방향 전환 완료로 본다.
        /// </summary>
        public float TurnCompleteAngleDegrees;
    }

    /// <summary>
    /// 렌더링 변형값.
    /// 추후 여러 Mesh/Material을 사용하는 경우 인덱스로 구분 가능.
    /// </summary>
    public struct UnitRenderVariant : IComponentData
    {
        public byte MaterialIndex;
        public byte MeshIndex;
    }

    /// <summary>
    /// Unit의 상위 상태와 방향/대열 상태.
    /// </summary>
    public struct UnitFsm : IComponentData
    {
        public UnitState State;
        public OrientationMode OriMode;
        public FormationMode FormMode;

        /// <summary>
        /// 곡선 대열 회전 보간 가중치.
        /// 0이면 직사각형 대열, 1이면 곡선 회전 대열.
        /// </summary>
        public float WheelingWeight;
    }

    /// <summary>
    /// Unit 이동 목표.
    /// 순간이동 좌표가 아니라 유닛이 걸어서 이동할 목표 좌표.
    /// </summary>
    public struct UnitMoveTarget : IComponentData
    {
        public float3 Position;
        public bool HasTarget;
    }

    /// <summary>
    /// Unit 이동 상태와 정규화 속도.
    /// </summary>
    public struct UnitLocomotion : IComponentData
    {
        public LocomotionState State;
        public float NormalizedSpeed;
    }

    /// <summary>
    /// 애니메이션 블렌드/렌더링에서 참고할 이동 기준값.
    /// </summary>
    public struct UnitAnimationReference : IComponentData
    {
        public float Speed;
        public float MotionSpeed;
        public bool Grounded;
    }

    /// <summary>
    /// 현재 Unit 단위 애니메이션 상태.
    /// 기본적으로 Unit 전체에 적용되는 상태이며,
    /// 병사 개별 Fight 여부는 SoldierCombatState에서 별도로 처리 가능.
    /// </summary>
    public struct UnitAnimationState : IComponentData
    {
        public UnitAnimationClipId Clip;
        public UnitAnimationClipId PreviousClip;
        public UnitAnimationMode Mode;
        public float NormalizedTime;
        public float PlaybackSpeed;
        public float BlendTime;
        public float LockRemainingTime;
        public byte Priority;
    }

    /// <summary>
    /// 외부 시스템이나 명령에서 애니메이션을 요청할 때 사용하는 컴포넌트.
    /// UnitAnimationStateSystem이 이 요청을 읽어 실제 UnitAnimationState로 반영.
    /// </summary>
    public struct UnitAnimationRequest : IComponentData
    {
        public bool HasRequest;
        public UnitAnimationClipId Clip;
        public UnitAnimationMode Mode;
        public float Duration;
        public float PlaybackSpeed;
        public byte Priority;
        public bool ClearAfterApply;
    }

    /// <summary>
    /// Unit 타입별 기본 애니메이션 클립 구성.
    /// 공격 방식별 시스템은 역할 이름 대신 이 profile의 PrimaryAttackClip을 사용한다.
    /// </summary>
    public struct UnitAnimationProfile : IComponentData
    {
        public UnitAnimationClipId IdleClip;
        public UnitAnimationClipId WalkClip;
        public UnitAnimationClipId RunClip;
        public UnitAnimationClipId PrimaryAttackClip;
    }

    /// <summary>
    /// 이동 속도에 따른 애니메이션 reference 값 보정용 파라미터.
    /// </summary>
    public struct UnitAnimationReferenceCalibration : IComponentData
    {
        public bool IdleGrounded;

        public float WalkSpeedMin;
        public float WalkSpeedMax;
        public float WalkMotionMin;
        public float WalkMotionMax;

        public float RunSpeedMin;
        public float RunSpeedMax;
        public float RunMotionMin;
        public float RunMotionMax;
    }

    /// <summary>
    /// Unit 본체 이동 파라미터.
    /// </summary>
    public struct UnitMoveParams : IComponentData
    {
        public float MaxSpeed;
        public float Accel;
        public float TurnRate;
        public float ArriveRadius;
        public float IdleSpeedEpsilon;
        public float RunThresholdNormalized;
    }

    /// <summary>
    /// Unit 공격 관련 파라미터.
    /// AttackRange는 병사 인스턴스 단위로 적과의 거리 판정에 사용 가능.
    /// </summary>
    public struct UnitAttackParams : IComponentData
    {
        /// <summary>
        /// 병사 인스턴스가 Fight 애니메이션을 재생할 공격 사거리.
        /// </summary>
        public float AttackRange;

        /// <summary>
        /// 공격 애니메이션 또는 공격 판정 주기.
        /// 현재는 확장용 값.
        /// </summary>
        public float AttackCooldown;
    }

    // ==========================================================
    //  Unit Combat Composition
    // ==========================================================

    /// <summary>
    /// 탐지 대상이 있을 때 Unit이 어떤 방식으로 전투 거리를 잡을지 결정한다.
    /// </summary>
    public enum AttackMovementPolicy : byte
    {
        /// <summary>
        /// 상대 Unit Footprint에 접촉할 때까지 접근한다. 기존 근접 유닛 기본 정책.
        /// </summary>
        Contact = 0,

        /// <summary>
        /// 접촉하지 않고 선호 거리대를 유지한다. 궁수 같은 원거리 유닛 기본 정책.
        /// </summary>
        PreferredRange = 1,

        /// <summary>
        /// 탐지 대상이 있어도 현재 위치를 유지하고 바라보기만 한다.
        /// </summary>
        HoldPosition = 2
    }

    /// <summary>
    /// 전투 중 이동/정지 거리 정책.
    /// UnitAttackParams.AttackRange는 공격 판정 사거리이고,
    /// 이 컴포넌트의 거리 값은 이동 정지와 거리 유지 정책에 사용한다.
    /// </summary>
    public struct UnitAttackMovePolicy : IComponentData
    {
        public AttackMovementPolicy Policy;

        /// <summary>
        /// PreferredRange 정책에서 유지하려는 이상적인 거리.
        /// </summary>
        public float PreferredRange;

        /// <summary>
        /// PreferredRange 정책에서 이 거리보다 가까우면 후퇴 또는 위치 유지 보정 대상.
        /// </summary>
        public float MinRange;

        /// <summary>
        /// PreferredRange 정책에서 이 거리보다 멀면 접근 대상.
        /// </summary>
        public float MaxRange;
    }

    /// <summary>
    /// 근접 공격 처리를 받을 Unit임을 표시하는 기능 태그.
    /// </summary>
    public struct MeleeAttackTag : IComponentData { }

    /// <summary>
    /// 근접 공격 전용 파라미터.
    /// 기존 UnitAttackParams와 의미가 중복되는 값은 실제 피해 시스템 연결 단계에서 하나로 정리한다.
    /// </summary>
    public struct MeleeAttackParams : IComponentData
    {
        public float Damage;
        public float AttackInterval;
        public float HitTimingNormalized;
    }

    /// <summary>
    /// 원거리 공격 처리를 받을 Unit임을 표시하는 기능 태그.
    /// </summary>
    public struct RangedAttackTag : IComponentData { }

    /// <summary>
    /// 원거리 공격 전용 파라미터.
    /// 투사체는 후속 확장 지점이며 1차 구현에서는 Entity.Null을 허용한다.
    /// </summary>
    public struct RangedAttackParams : IComponentData
    {
        public Entity ProjectilePrefab;

        public float ProjectileSpeed;
        public float ProjectileArcHeight;

        public float PreferredRange;
        public float MinRange;
        public float MaxRange;

        public float VolleyInterval;
        public int ProjectilesPerVolley;

        public float ShootTimingNormalized;
    }

    /// <summary>
    /// 데모 Archer와 동일한 GameObject 화살 프리팹을 발사하기 위한 managed visual 참조.
    /// 실제 전투 판정은 RangedAttackParams가 담당하고, 이 컴포넌트는 화면 표시만 담당한다.
    /// </summary>
    public sealed class RangedProjectileVisualPrefab : IComponentData
    {
        public GameObject Prefab;
    }

    /// <summary>
    /// 원거리 공격 런타임 상태.
    /// RangedCombatStateSystem에서 사격 가능 여부와 쿨다운을 갱신한다.
    /// </summary>
    public struct RangedAttackState : IComponentData
    {
        public float Cooldown;
        public float VolleyTimer;
        public float ShootAnimationTimer;
        public bool IsShooting;
        public bool ShootStarted;
        public bool FireTriggered;
        public bool ProjectileReleased;
    }

    /// <summary>
    /// Unit Footprint 간 접촉/겹침 제어 파라미터.
    /// 유닛을 원형 물체처럼 완전히 밀어내지 않고,
    /// Total War식 전투 접촉선에 가까운 얕은 겹침을 허용하기 위한 설정이다.
    /// </summary>
    public struct UnitContactParams : IComponentData
    {
        /// <summary>
        /// 적 유닛끼리 허용할 대형 겹침 비율.
        /// 0이면 겹침 허용 없음, 0.33이면 약 1/3 겹침 허용.
        /// </summary>
        public float EnemyAllowedOverlapRatio;

        /// <summary>
        /// 같은 팩션 유닛끼리 허용할 대형 겹침 비율.
        /// 보통 적 유닛보다 훨씬 낮게 둔다.
        /// </summary>
        public float SameFactionAllowedOverlapRatio;

        /// <summary>
        /// 접촉 거리 주변에서 정지 상태로 인정할 폭.
        /// 너무 작으면 전투선 앞에서 진동할 수 있다.
        /// </summary>
        public float ContactBandWidth;

        /// <summary>
        /// Footprint 계산 후 추가로 둘 여유 거리.
        /// 양수면 더 떨어지고, 0이면 실제 footprint 기준.
        /// </summary>
        public float ContactPadding;

        /// <summary>
        /// 적 유닛과 과하게 겹쳤을 때의 보정 강도.
        /// 너무 크면 전투선이 벌어진다.
        /// </summary>
        public float EnemyCorrectionStrength;

        /// <summary>
        /// 같은 팩션 유닛과 겹쳤을 때의 보정 강도.
        /// 같은 편끼리는 더 강하게 분리하는 편이 자연스럽다.
        /// </summary>
        public float SameFactionCorrectionStrength;

        /// <summary>
        /// 전투 상태에서 회피 보정을 얼마나 약화할지.
        /// 0이면 전투 중 회피 없음, 1이면 일반 회피와 동일.
        /// </summary>
        public float EngagingAvoidanceScale;

        /// <summary>
        /// 한 tick에서 허용되는 최대 위치 보정 거리.
        /// 순간이동처럼 튀는 현상을 막는다.
        /// </summary>
        public float MaxCorrectionPerTick;

        /// <summary>
        /// 서로 파고드는 방향의 속도를 얼마나 감쇠할지.
        /// </summary>
        public float VelocityDamping;
    }

    /// <summary>
    /// Unit 본체 속도.
    /// </summary>
    public struct UnitVelocity : IComponentData
    {
        public float3 Value;
    }

    /// <summary>
    /// 이전 프레임 위치.
    /// 보간, 디버그, 스냅샷 등에 사용 가능.
    /// </summary>
    public struct UnitPreviousPos : IComponentData
    {
        public float3 Value;
    }

    /// <summary>
    /// 이전 프레임 회전.
    /// 보간, 디버그, 스냅샷 등에 사용 가능.
    /// </summary>
    public struct UnitPreviousRot : IComponentData
    {
        public quaternion Value;
    }

    /// <summary>
    /// 대열 조향 정보.
    /// UnitSimulationSystem에서 계산하고 CrowdSimulationSystem에서 참조.
    /// </summary>
    public struct FormationSteering : IComponentData
    {
        public float3 DesiredDirection;
        public float DesiredSpeed;
        public float3 CurrentForward;
        public float AngularVelocity;
    }

    // ==========================================================
    //  Tick + Command stream
    // ==========================================================

    /// <summary>
    /// 고정 시뮬레이션 tick.
    /// 명령 적용 순서와 스냅샷 기준으로 사용.
    /// </summary>
    public struct SimTick : IComponentData
    {
        public uint Value;
    }

    /// <summary>
    /// UnitCommand 버퍼에서 사용할 명령 타입.
    /// </summary>
    public enum UnitCommandType : byte
    {
        None = 0,
        MoveTo = 1,
        SetSwarmParams = 2,
        SetAnimationState = 3,
        ClearAnimationState = 4,
        Fight = 5
    }

    /// <summary>
    /// tick 기반 Unit 명령.
    /// 이동, 군집 파라미터 변경, 애니메이션 요청을 하나의 버퍼로 처리.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct UnitCommand : IBufferElementData
    {
        public uint Tick;
        public UnitCommandType Type;

        public float3 TargetPos;

        public int SoldierCount;
        public int Columns;
        public float SpacingX;
        public float SpacingZ;
        public float BaseJitter;
        public uint Seed;

        public UnitAnimationClipId AnimationClip;
        public UnitAnimationMode AnimationMode;
        public float AnimationDuration;
        public float AnimationPlaybackSpeed;
        public byte AnimationPriority;
        public bool AnimationClearAfterApply;
    }

    /// <summary>
    /// 마지막으로 적용한 명령 tick.
    /// 같은 명령의 중복 적용을 방지.
    /// </summary>
    public struct UnitCommandCursor : IComponentData
    {
        public uint LastAppliedTick;
    }

    // ==========================================================
    //  Swarm / Soldier
    // ==========================================================

    /// <summary>
    /// Unit 하나가 포함하는 병사 인스턴스 대열 파라미터.
    /// </summary>
    public struct SwarmParams : IComponentData
    {
        public int SoldierCount;
        public int Columns;
        public float SpacingX;
        public float SpacingZ;
        public float BaseJitter;
        public uint Seed;
    }

    /// <summary>
    /// SwarmInitSystem이 마지막으로 생성한 대열 정보.
    /// SwarmParams 변경 여부를 감지하는 캐시 역할.
    /// </summary>
    public struct SwarmRuntime : IComponentData
    {
        public int GeneratedCount;
        public int GeneratedColumns;
        public float GeneratedSpacingX;
        public float GeneratedSpacingZ;
        public uint GeneratedSeed;
    }

    /// <summary>
    /// 병사 인스턴스 간 이동/분리 관련 파라미터.
    /// </summary>
    public struct SwarmCrowdParams : IComponentData
    {
        public float MaxLocalSpeed;
        public float FollowGain;
        public float SeparationWeight;
        public float PersonalSpace;
    }

    /// <summary>
    /// Unit 상태별 대열 응집도.
    /// </summary>
    public struct SwarmStateTuning : IComponentData
    {
        public float IdleTightness;
        public float MovingTightness;
        public float EngagingTightness;
        public float RetreatingTightness;
    }

    /// <summary>
    /// 병사 인스턴스가 전투 상태에서 기본 슬롯을 얼마나 벗어나 적 유닛 쪽으로 전진할지 제어하는 파라미터.
    /// CrowdSimulationSystem에서 사용한다.
    /// </summary>
    public struct SwarmEngagementParams : IComponentData
    {
        /// <summary>
        /// 전투 상태에서 병사 인스턴스가 기본 슬롯에서 전방으로 이동할 수 있는 최대 거리.
        /// </summary>
        public float MaxEngageAdvance;

        /// <summary>
        /// 전열 병사의 전진 가중치.
        /// slot.Frontness와 곱해 사용한다.
        /// </summary>
        public float FrontAdvanceScale;

        /// <summary>
        /// 후열 병사의 최소 전진 가중치.
        /// 0이면 후열은 거의 고정된다.
        /// </summary>
        public float RearAdvanceScale;

        /// <summary>
        /// 전투 중 좌우 흐트러짐 허용 거리.
        /// 너무 크면 대형이 무너진다.
        /// </summary>
        public float LateralFreedom;

        /// <summary>
        /// 상대 유닛 Footprint 경계에서 약간 바깥쪽을 목표로 삼기 위한 여유 거리.
        /// </summary>
        public float TargetFootprintPadding;

        /// <summary>
        /// 전투 전진 목표로 끌어당기는 강도.
        /// </summary>
        public float EngageMoveStrength;

        /// <summary>
        /// 전투 중에도 원래 슬롯을 유지하려는 가중치.
        /// 높을수록 직사각형 대형이 강하게 유지된다.
        /// </summary>
        public float SlotHoldStrength;

        /// <summary>
        /// 전투 중 FacingYaw가 목표 방향으로 반응하는 강도.
        /// </summary>
        public float FacingSharpness;
    }

    /// <summary>
    /// 병사 개별 애니메이션 근사와 렌더링 보정값.
    /// </summary>
    public struct SwarmAnimationApproximationTuning : IComponentData
    {
        public float IdleVelocityScale;
        public float WalkVelocityScale;
        public float RunVelocityScale;

        public float IdleSettleScale;
        public float WalkSettleScale;
        public float RunSettleScale;

        public float IdlePhaseScale;
        public float WalkPhaseScale;
        public float RunPhaseScale;

        public float IdleSwayScale;
        public float WalkSwayScale;
        public float RunSwayScale;

        public float IdleRenderScale;
        public float WalkRenderScale;
        public float RunRenderScale;

        public float VisualWalkSpeedThreshold;
        public float VisualRunSpeedThreshold;
        public float VisualMotionSpeedDivisor;
    }

    /// <summary>
    /// OneShot 애니메이션 시작 시점을 병사별로 어긋나게 하는 튜닝값.
    /// </summary>
    public struct SwarmAnimationDesyncTuning : IComponentData
    {
        public float OneShotDelayMin;
        public float OneShotDelayMax;
    }

    /// <summary>
    /// 병사의 기본 대열 슬롯.
    /// BaseLocalPos는 대열 기준 로컬 좌표.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct SoldierSlot : IBufferElementData
    {
        public float3 BaseLocalPos;
        public int Row;

        /// <summary>
        /// 앞열에 가까울수록 1, 후열에 가까울수록 0.
        /// </summary>
        public float Frontness;

        /// <summary>
        /// 좌우 정규화 좌표.
        /// -1이면 좌측, 1이면 우측.
        /// </summary>
        public float LateralNormalized;

        /// <summary>
        /// 행 정규화 값.
        /// 현재는 Frontness와 동일 기준으로 사용.
        /// </summary>
        public float RowNormalized;
    }

    /// <summary>
    /// 병사 인스턴스의 현재 로컬 위치, 속도, 애니메이션 위상 속도, 바라보는 방향.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct SoldierAgent : IBufferElementData
    {
        public float3 CurrentLocalPos;
        public float3 Velocity;
        public float AnimPhaseSpeed;

        /// <summary>
        /// 3D 렌더링을 위한 병사 개별 회전 방향.
        /// 라디안 단위 Yaw.
        /// </summary>
        public float FacingYaw;
    }

    /// <summary>
    /// 병사별 랜덤 편차.
    /// 이동 지연, 속도 배율, 애니메이션 위상, OneShot 시작 지연을 저장.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct SoldierVariance : IBufferElementData
    {
        public uint Seed;
        public float MoveDelay;
        public float SpeedMul;
        public float Phase;
        public float AnimStartDelay;
    }

    /// <summary>
    /// 병사 인스턴스 단위 전투 상태.
    /// 사거리 안에 있는 병사만 Fight 애니메이션을 재생하려면 렌더러가 이 버퍼를 참조해야 함.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct SoldierCombatState : IBufferElementData
    {
        /// <summary>
        /// 현재 이 병사가 공격 사거리 안에 적을 가지고 있는지 여부.
        /// </summary>
        public bool InAttackRange;

        /// <summary>
        /// 병사가 바라보거나 공격해야 할 목표 위치.
        /// </summary>
        public float3 TargetPos;

        /// <summary>
        /// 개별 공격 위상 또는 쿨다운 타이머.
        /// 현재는 확장용 값.
        /// </summary>
        public float AttackTimer;
    }

    // ==========================================================
    //  Snapshot
    // ==========================================================

    /// <summary>
    /// Unit 스냅샷 ring buffer 설정.
    /// </summary>
    public struct SnapshotConfig : IComponentData
    {
        public int Capacity;
        public uint Interval;
    }

    /// <summary>
    /// Unit 스냅샷 ring buffer 상태.
    /// </summary>
    public struct SnapshotRingState : IComponentData
    {
        public int Head;
        public bool Initialized;
        public uint LatestTick;
    }

    /// <summary>
    /// 디버그, 리플레이, 검증용 Unit 상태 스냅샷.
    /// </summary>
    [InternalBufferCapacity(0)]
    public struct UnitSnapshot : IBufferElementData
    {
        public uint Tick;
        public float3 Pos;
        public float3 Vel;
        public UnitState State;

        public bool HasTarget;
        public float3 TargetPos;

        public int SoldierCount;
        public int Columns;
        public float SpacingX;
        public float SpacingZ;
        public uint Seed;
    }
}
