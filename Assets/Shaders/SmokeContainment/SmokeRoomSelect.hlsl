// The room boxes shared by every contained smoke and flame particle, and the
// search that picks which box a particle belongs to.
//
// The search loops over every box, so it runs once per particle corner in the
// vertex stage: the VFX outputs call it from a Custom HLSL operator and hand
// the result to the Shader Graph as ParticleRoom or OwnerRoom. Running it per
// pixel, as it once did, cost about as much as the rest of the smoke shader.
// SmokeRoomClip.hlsl then fades each pixel against that one room only.
//
// Room boxes come from SmokeRoomClipVolumes at runtime. With no rooms set,
// every search returns -1 and nothing is clipped.

#ifndef SMOKE_ROOM_SELECT_INCLUDED
#define SMOKE_ROOM_SELECT_INCLUDED

#define SMOKE_ROOM_CLIP_MAX_ROOMS 64

// xyz: box center in world space. w: x of the box's local right axis.
float4 _SmokeRoomCenters[SMOKE_ROOM_CLIP_MAX_ROOMS];
// xyz: half size of the box. w: z of the box's local right axis.
float4 _SmokeRoomExtents[SMOKE_ROOM_CLIP_MAX_ROOMS];
// x: index of the first box of this box's room. y: number of boxes in that room.
// A room that is not a rectangle is several overlapping boxes.
float4 _SmokeRoomGroups[SMOKE_ROOM_CLIP_MAX_ROOMS];
float _SmokeRoomCount;
// Width, in meters, over which smoke fades out at a room's edge.
float _SmokeRoomFade;

float3 SmokeRoomLocal(float3 worldPosition, int room)
{
    float3 offset = worldPosition - _SmokeRoomCenters[room].xyz;
    float c = _SmokeRoomCenters[room].w;
    float s = _SmokeRoomExtents[room].w;
    return float3(c * offset.x + s * offset.z, offset.y, -s * offset.x + c * offset.z);
}

// Distance from a point to the nearest face of a room's box. Positive inside.
float SmokeRoomDepth(float3 worldPosition, int room)
{
    float3 inside = _SmokeRoomExtents[room].xyz - abs(SmokeRoomLocal(worldPosition, room));
    return min(inside.x, min(inside.y, inside.z));
}

// Same, measured across the floor plan only, so a particle that has risen
// above the ceiling still belongs to the room below it.
float SmokeRoomPlanDepth(float3 worldPosition, int room)
{
    float3 inside = _SmokeRoomExtents[room].xyz - abs(SmokeRoomLocal(worldPosition, room));
    return min(inside.x, inside.z);
}

// A smoke particle belongs to the room whose box holds its center, or the
// nearest one when the center has drifted into a wall or a doorway.
// Returns that box's index, or -1 when no rooms are set.
float SmokeParticleRoom(float3 particleCenter)
{
    int count = (int)_SmokeRoomCount;
    if (count <= 0)
        return -1.0;

    int box = 0;
    float best = -1.0e9;
    for (int i = 0; i < SMOKE_ROOM_CLIP_MAX_ROOMS; i++)
    {
        if (i >= count)
            break;

        float depth = SmokeRoomPlanDepth(particleCenter, i);
        if (depth > best)
        {
            best = depth;
            box = i;
        }
    }

    return (float)box;
}

// Flames and embers belong to the room that holds the object that is burning
// (Owner), not to the particle's own center: flames rise and flicker into the
// wall behind them, and embers fly through it. An owner outside every listed
// room gets -1 and is not clipped, so a fire in a room the smoke has not
// measured, or on another floor, still shows.
float FlameOwnerRoom(float3 owner)
{
    int count = (int)_SmokeRoomCount;
    if (count <= 0)
        return -1.0;

    int box = 0;
    float best = -1.0e9;
    for (int i = 0; i < SMOKE_ROOM_CLIP_MAX_ROOMS; i++)
    {
        if (i >= count)
            break;

        float depth = SmokeRoomDepth(owner, i);
        if (depth > best)
        {
            best = depth;
            box = i;
        }
    }

    return best < 0.0 ? -1.0 : (float)box;
}

#endif
