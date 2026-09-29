using Unity.Mathematics;

namespace Swarm
{
    internal static class LegionSpawnPlacement
    {
        public static float GetCenterSpacing(
            in SwarmParams swarmParams,
            float configuredSpacing,
            float uniformScale = 1f)
        {
            int soldierCount = math.max(1, swarmParams.SoldierCount);
            int columns = math.clamp(swarmParams.Columns, 1, soldierCount);
            int rows = (soldierCount + columns - 1) / columns;

            float spacingX = math.abs(swarmParams.SpacingX);
            float spacingZ = math.abs(swarmParams.SpacingZ);
            float jitterMargin = math.max(0f, swarmParams.BaseJitter) * 2f;
            float scale = math.max(0.0001f, math.abs(uniformScale));

            float formationWidth =
                ((columns - 1) * spacingX + jitterMargin) * scale;
            float formationDepth =
                ((rows - 1) * spacingZ + jitterMargin) * scale;

            float soldierClearance = math.max(spacingX, spacingZ) * scale;
            float requiredSpacing =
                math.max(formationWidth, formationDepth) + soldierClearance;

            return math.max(math.max(0f, configuredSpacing), requiredSpacing);
        }
    }
}
