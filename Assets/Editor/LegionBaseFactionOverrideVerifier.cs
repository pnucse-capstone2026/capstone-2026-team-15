using Detection;
using Swarm;
using Unity.Entities;
using UnityEditor;
using UnityEngine;

public static class LegionBaseFactionOverrideVerifier
{
    private const string MenuPath = "Tools/Swarm/Verify Legion Base Faction Overrides";

    [MenuItem(MenuPath)]
    public static void Verify()
    {
        World world = World.DefaultGameObjectInjectionWorld;

        if (world == null || !world.IsCreated)
        {
            Debug.LogWarning("LegionBase faction verification failed: Default ECS world is not available.");
            return;
        }

        EntityManager entityManager = world.EntityManager;

        using EntityQuery query = entityManager.CreateEntityQuery(
            ComponentType.ReadOnly<LegionBaseTag>(),
            ComponentType.ReadOnly<LegionBaseUnitOverrideData>(),
            ComponentType.ReadOnly<LegionBaseUnitElement>());

        using var baseEntities = query.ToEntityArray(Unity.Collections.Allocator.Temp);

        if (baseEntities.Length == 0)
        {
            Debug.LogWarning("LegionBase faction verification found no LegionBase entities.");
            return;
        }

        int checkedUnits = 0;
        int mismatchedUnits = 0;
        int missingDetectionTags = 0;
        int missingSpawnedUnits = 0;

        for (int i = 0; i < baseEntities.Length; i++)
        {
            Entity baseEntity = baseEntities[i];
            LegionBaseUnitOverrideData overrideData =
                entityManager.GetComponentData<LegionBaseUnitOverrideData>(baseEntity);
            DynamicBuffer<LegionBaseUnitElement> spawnedUnits =
                entityManager.GetBuffer<LegionBaseUnitElement>(baseEntity, true);

            for (int j = 0; j < spawnedUnits.Length; j++)
            {
                Entity unitEntity = spawnedUnits[j].UnitEntity;

                if (!entityManager.Exists(unitEntity))
                {
                    missingSpawnedUnits++;
                    Debug.LogWarning(
                        $"LegionBase faction verification: Base {baseEntity} references missing Unit entity at index {spawnedUnits[j].Index}.");
                    continue;
                }

                if (!entityManager.HasComponent<DetectionTag>(unitEntity))
                {
                    missingDetectionTags++;
                    Debug.LogWarning(
                        $"LegionBase faction verification: Unit {unitEntity} from Base {baseEntity} has no DetectionTag.");
                    continue;
                }

                DetectionTag detectionTag =
                    entityManager.GetComponentData<DetectionTag>(unitEntity);

                checkedUnits++;

                if (detectionTag.FactionId == overrideData.FactionId)
                {
                    continue;
                }

                mismatchedUnits++;
                Debug.LogWarning(
                    "LegionBase faction verification mismatch: " +
                    $"Base {baseEntity} expected FactionId {overrideData.FactionId}, " +
                    $"but Unit {unitEntity} has FactionId {detectionTag.FactionId}.");
            }
        }

        if (checkedUnits == 0 && missingSpawnedUnits == 0 && missingDetectionTags == 0)
        {
            Debug.LogWarning(
                "LegionBase faction verification found LegionBase entities, but no spawned Unit entries. " +
                "Enter Play Mode with spawnOnStart enabled before running the verifier.");
            return;
        }

        if (mismatchedUnits == 0 && missingDetectionTags == 0 && missingSpawnedUnits == 0)
        {
            Debug.Log(
                $"LegionBase faction verification passed. Checked {checkedUnits} spawned Unit entities.");
            return;
        }

        Debug.LogWarning(
            "LegionBase faction verification completed with issues. " +
            $"Checked={checkedUnits}, Mismatched={mismatchedUnits}, " +
            $"MissingDetectionTag={missingDetectionTags}, MissingUnitEntity={missingSpawnedUnits}.");
    }
}
