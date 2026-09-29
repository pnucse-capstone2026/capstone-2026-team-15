using Swarm;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(UnitAuthoring))]
public class RangedUnitAuthoring : MonoBehaviour
{
    [Header("Range Policy")]
    [Min(0f)][SerializeField] private float preferredRange = 18f;
    [Min(0f)][SerializeField] private float minRange = 0f;
    [Min(0f)][SerializeField] private float maxRange = 22f;

    [Header("Volley")]
    [Min(0f)][SerializeField] private float volleyInterval = 2f;
    [Min(1)][SerializeField] private int projectilesPerVolley = 16;
    [Range(0f, 1f)][SerializeField] private float shootTimingNormalized = 0.56f;

    [Header("Projectile")]
    [SerializeField] private GameObject projectilePrefab;
    [Min(0f)][SerializeField] private float projectileSpeed = 25f;
    [Min(0f)][SerializeField] private float projectileArcHeight = 6f;

    public UnitAttackMovePolicy BuildAttackMovePolicy()
    {
        GetSanitizedRanges(out float preferred, out float min, out float max);

        return new UnitAttackMovePolicy
        {
            Policy = AttackMovementPolicy.PreferredRange,
            PreferredRange = preferred,
            MinRange = min,
            MaxRange = max
        };
    }

    public UnitAnimationProfile BuildAnimationProfile()
    {
        return new UnitAnimationProfile
        {
            IdleClip = UnitAnimationClipId.Idle,
            WalkClip = UnitAnimationClipId.Walk,
            RunClip = UnitAnimationClipId.Run,
            PrimaryAttackClip = UnitAnimationClipId.BowShoot
        };
    }

    private void GetSanitizedRanges(out float preferred, out float min, out float max)
    {
        min = math.max(0f, minRange);
        max = math.max(min, maxRange);
        preferred = math.clamp(math.max(0f, preferredRange), min, max);
    }

    class Baker : Baker<RangedUnitAuthoring>
    {
        public override void Bake(RangedUnitAuthoring authoring)
        {
            Entity entity =
                GetEntity(TransformUsageFlags.Dynamic | TransformUsageFlags.Renderable);

            authoring.GetSanitizedRanges(out float preferred, out float min, out float max);

            Entity projectileEntity = Entity.Null;
            if (authoring.projectilePrefab != null)
            {
                projectileEntity =
                    GetEntity(authoring.projectilePrefab, TransformUsageFlags.Dynamic);

                AddComponentObject(entity, new RangedProjectileVisualPrefab
                {
                    Prefab = authoring.projectilePrefab
                });
            }

            AddComponent<RangedAttackTag>(entity);

            AddComponent(entity, new RangedAttackParams
            {
                ProjectilePrefab = projectileEntity,
                ProjectileSpeed = math.max(0f, authoring.projectileSpeed),
                ProjectileArcHeight = math.max(0f, authoring.projectileArcHeight),
                PreferredRange = preferred,
                MinRange = min,
                MaxRange = max,
                VolleyInterval = math.max(0f, authoring.volleyInterval),
                ProjectilesPerVolley = math.max(1, authoring.projectilesPerVolley),
                ShootTimingNormalized = math.saturate(authoring.shootTimingNormalized)
            });

            AddComponent(entity, new RangedAttackState
            {
                Cooldown = 0f,
                VolleyTimer = 0f,
                ShootAnimationTimer = 0f,
                IsShooting = false,
                ShootStarted = false,
                FireTriggered = false,
                ProjectileReleased = false
            });
        }
    }
}
