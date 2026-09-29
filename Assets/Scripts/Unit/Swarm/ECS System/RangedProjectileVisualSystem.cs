using Detection;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace Swarm
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(RangedCombatStateSystem))]
    internal partial class RangedProjectileVisualSystem : SystemBase
    {
        private const float SpawnHeight = 1.25f;
        private const float SpawnForwardOffset = 0.8f;
        private const float TargetHeight = 1.0f;
        private const float DefaultProjectileSpeed = 25f;

        internal static bool RenderingEnabled { get; set; } = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRenderingEnabled()
        {
            RenderingEnabled = true;
        }

        protected override void OnUpdate()
        {
            if (!RenderingEnabled)
            {
                return;
            }

            ComponentLookup<LocalTransform> targetTransforms =
                GetComponentLookup<LocalTransform>(true);

            ComponentLookup<SwarmParams> targetSwarmParams =
                GetComponentLookup<SwarmParams>(true);

            Entities
                .WithoutBurst()
                .WithAll<RangedAttackTag>()
                .ForEach((
                    Entity entity,
                    RangedProjectileVisualPrefab visualPrefab,
                    in RangedAttackParams rangedParams,
                    in RangedAttackState rangedState,
                    in DetectionTarget detectionTarget,
                    in LocalTransform transform,
                    in DynamicBuffer<SoldierAgent> soldiers) =>
                {
                    if (!rangedState.FireTriggered ||
                        !detectionTarget.HasTarget ||
                        visualPrefab.Prefab == null)
                    {
                        return;
                    }

                    SpawnProjectiles(
                        visualPrefab.Prefab,
                        in rangedParams,
                        in detectionTarget,
                        in transform,
                        soldiers,
                        entity,
                        targetTransforms,
                        targetSwarmParams);
                })
                .Run();
        }

        private static void SpawnProjectiles(
            GameObject projectilePrefab,
            in RangedAttackParams rangedParams,
            in DetectionTarget detectionTarget,
            in LocalTransform transform,
            DynamicBuffer<SoldierAgent> soldiers,
            Entity selfEntity,
            ComponentLookup<LocalTransform> targetTransforms,
            ComponentLookup<SwarmParams> targetSwarmParams)
        {
            float3 direction =
                detectionTarget.TargetPos -
                transform.Position;
            direction.y = 0f;

            if (math.lengthsq(direction) <= 0.0001f)
            {
                return;
            }

            direction = math.normalize(direction);

            int requestedCount =
                math.max(
                    1,
                    rangedParams.ProjectilesPerVolley);

            int soldierCount =
                soldiers.IsCreated
                    ? soldiers.Length
                    : 0;

            int spawnCount =
                soldierCount > 0
                    ? math.min(requestedCount, soldierCount)
                    : requestedCount;

            bool hasTargetFootprint =
                TryGetTargetFootprint(
                    selfEntity,
                    in detectionTarget,
                    targetTransforms,
                    targetSwarmParams,
                    out float3 targetPosition,
                    out quaternion targetRotation,
                    out SwarmParams targetSwarm);

            for (int i = 0; i < spawnCount; i++)
            {
                Vector3 startPosition =
                    GetSpawnPosition(
                        in transform,
                        soldiers,
                        (Vector3)direction,
                        i,
                        spawnCount,
                        soldierCount);

                Vector3 endPosition =
                    hasTargetFootprint
                        ? GetFootprintTargetPosition(
                            targetPosition,
                            targetRotation,
                            in targetSwarm,
                            i,
                            spawnCount)
                        : GetFallbackTargetPosition(
                            targetPosition,
                            (Vector3)direction,
                            i,
                            spawnCount);

                SpawnProjectile(
                    projectilePrefab,
                    startPosition,
                    endPosition,
                    math.max(0f, rangedParams.ProjectileSpeed),
                    math.max(0f, rangedParams.ProjectileArcHeight));
            }
        }

        private static Vector3 GetSpawnPosition(
            in LocalTransform transform,
            DynamicBuffer<SoldierAgent> soldiers,
            Vector3 direction,
            int index,
            int spawnCount,
            int soldierCount)
        {
            if (soldierCount <= 0)
            {
                return
                    (Vector3)(transform.Position + (float3)direction * SpawnForwardOffset) +
                    Vector3.up * SpawnHeight;
            }

            int soldierIndex =
                GetSoldierIndex(
                    index,
                    spawnCount,
                    soldierCount);

            SoldierAgent soldier =
                soldiers[soldierIndex];

            return
                (Vector3)(transform.Position + soldier.CurrentLocalPos) +
                direction * SpawnForwardOffset +
                Vector3.up * SpawnHeight;
        }

        private static void SpawnProjectile(
            GameObject projectilePrefab,
            Vector3 startPosition,
            Vector3 targetPosition,
            float speed,
            float arcHeight)
        {
            Vector3 initialDirection =
                targetPosition - startPosition;

            if (initialDirection.sqrMagnitude <= 0.0001f)
            {
                return;
            }

            GameObject projectile =
                Object.Instantiate(
                    projectilePrefab,
                    startPosition,
                    Quaternion.LookRotation(initialDirection.normalized, Vector3.up));

            var mover =
                projectile.GetComponent<RangedProjectileVisualMover>();

            if (mover == null)
            {
                mover =
                    projectile.AddComponent<RangedProjectileVisualMover>();
            }

            mover.Initialize(
                startPosition,
                targetPosition,
                speed > 0f ? speed : DefaultProjectileSpeed,
                arcHeight);
        }

        private static bool TryGetTargetFootprint(
            Entity selfEntity,
            in DetectionTarget detectionTarget,
            ComponentLookup<LocalTransform> targetTransforms,
            ComponentLookup<SwarmParams> targetSwarmParams,
            out float3 targetPosition,
            out quaternion targetRotation,
            out SwarmParams targetSwarm)
        {
            targetPosition = detectionTarget.TargetPos;
            targetRotation = quaternion.identity;
            targetSwarm = default;

            Entity targetEntity =
                detectionTarget.CenterTarget;

            if (targetEntity == Entity.Null ||
                targetEntity == selfEntity ||
                !targetTransforms.HasComponent(targetEntity) ||
                !targetSwarmParams.HasComponent(targetEntity))
            {
                return false;
            }

            LocalTransform targetTransform =
                targetTransforms[targetEntity];

            targetPosition = targetTransform.Position;
            targetRotation = targetTransform.Rotation;
            targetSwarm = targetSwarmParams[targetEntity];

            return true;
        }

        private static Vector3 GetFootprintTargetPosition(
            float3 targetPosition,
            quaternion targetRotation,
            in SwarmParams targetSwarm,
            int index,
            int count)
        {
            GetFormationHalfExtents(
                in targetSwarm,
                out float halfWidth,
                out float halfDepth);

            if (halfWidth <= 0.0001f && halfDepth <= 0.0001f)
            {
                return
                    (Vector3)targetPosition +
                    Vector3.up * TargetHeight;
            }

            int columns =
                math.max(
                    1,
                    math.min(
                        count,
                        targetSwarm.Columns > 0
                            ? targetSwarm.Columns
                            : Mathf.CeilToInt(Mathf.Sqrt(count))));

            int rows =
                math.max(
                    1,
                    Mathf.CeilToInt(count / (float)columns));

            int col =
                index % columns;

            int row =
                index / columns;

            float x =
                columns <= 1
                    ? 0f
                    : math.lerp(
                        -halfWidth,
                        halfWidth,
                        col / (float)(columns - 1));

            float z =
                rows <= 1
                    ? 0f
                    : math.lerp(
                        -halfDepth,
                        halfDepth,
                        row / (float)(rows - 1));

            float3 localPoint =
                new float3(
                    x,
                    TargetHeight,
                    z);

            return
                (Vector3)(
                    targetPosition +
                    math.rotate(
                        targetRotation,
                        localPoint));
        }

        private static Vector3 GetFallbackTargetPosition(
            float3 targetPosition,
            Vector3 forward,
            int index,
            int count)
        {
            Vector3 target =
                (Vector3)targetPosition +
                Vector3.up * TargetHeight;

            if (count <= 1)
            {
                return target;
            }

            float offset =
                Mathf.Lerp(
                    -1f,
                    1f,
                    index / (float)(count - 1));

            Vector3 right =
                Vector3.Cross(Vector3.up, forward).normalized;

            return
                target +
                right * offset;
        }

        private static void GetFormationHalfExtents(
            in SwarmParams swarmParams,
            out float halfWidth,
            out float halfDepth)
        {
            int columns =
                math.max(
                    1,
                    swarmParams.Columns);

            int soldierCount =
                math.max(
                    1,
                    swarmParams.SoldierCount);

            int rows =
                math.max(
                    1,
                    (soldierCount + columns - 1) / columns);

            halfWidth =
                math.max(
                    0f,
                    (columns - 1) *
                    math.max(0f, swarmParams.SpacingX) *
                    0.5f);

            halfDepth =
                math.max(
                    0f,
                    (rows - 1) *
                    math.max(0f, swarmParams.SpacingZ) *
                    0.5f);
        }

        private static int GetSoldierIndex(
            int projectileIndex,
            int projectileCount,
            int soldierCount)
        {
            if (soldierCount <= 1 || projectileCount <= 1)
            {
                return 0;
            }

            float t =
                projectileIndex /
                (float)(projectileCount - 1);

            return Mathf.Clamp(
                Mathf.RoundToInt(t * (soldierCount - 1)),
                0,
                soldierCount - 1);
        }
    }

    internal sealed class RangedProjectileVisualMover : MonoBehaviour
    {
        private const float MinProjectileDuration = 0.1f;
        private const float MaxProjectileDuration = 3.0f;

        private Vector3 startPosition;
        private Vector3 targetPosition;
        private Vector3 previousPosition;
        private float arcHeight;
        private float duration = 1f;
        private float elapsed;

        public void Initialize(
            Vector3 start,
            Vector3 target,
            float speed,
            float arc)
        {
            startPosition = start;
            targetPosition = target;
            previousPosition = start;
            arcHeight = Mathf.Max(0f, arc);
            elapsed = 0f;

            float distance =
                Vector3.Distance(
                    startPosition,
                    targetPosition);

            duration =
                Mathf.Clamp(
                    distance / Mathf.Max(0.001f, speed),
                    MinProjectileDuration,
                    MaxProjectileDuration);

            transform.position = startPosition;
        }

        private void Update()
        {
            elapsed += Time.deltaTime;

            float t =
                Mathf.Clamp01(
                    elapsed / duration);

            Vector3 nextPosition =
                Vector3.Lerp(
                    startPosition,
                    targetPosition,
                    t);

            nextPosition.y +=
                Mathf.Sin(t * Mathf.PI) *
                arcHeight;

            Vector3 movement =
                nextPosition - previousPosition;

            if (movement.sqrMagnitude > 0.000001f)
            {
                transform.rotation =
                    Quaternion.LookRotation(
                        movement.normalized,
                        Vector3.up);
            }

            transform.position = nextPosition;
            previousPosition = nextPosition;

            if (t >= 1f)
            {
                Destroy(gameObject);
            }
        }
    }
}
