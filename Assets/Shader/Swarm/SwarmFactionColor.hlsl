#ifndef SWARM_FACTION_COLOR_INCLUDED
#define SWARM_FACTION_COLOR_INCLUDED

float4 GetSwarmFactionColor(float factionIdValue)
{
    int factionId = (int)round(factionIdValue);

    if (factionId < 0)
    {
        return float4(0.55, 0.55, 0.55, 1.00);
    }

    switch (factionId % 8)
    {
        case 0:
            return float4(0.20, 0.45, 1.00, 1.00);
        case 1:
            return float4(1.00, 0.22, 0.18, 1.00);
        case 2:
            return float4(0.18, 0.80, 0.32, 1.00);
        case 3:
            return float4(1.00, 0.86, 0.18, 1.00);
        case 4:
            return float4(0.15, 0.85, 0.95, 1.00);
        case 5:
            return float4(0.90, 0.25, 1.00, 1.00);
        case 6:
            return float4(1.00, 0.55, 0.12, 1.00);
        default:
            return float4(0.92, 0.92, 0.92, 1.00);
    }
}

#endif
