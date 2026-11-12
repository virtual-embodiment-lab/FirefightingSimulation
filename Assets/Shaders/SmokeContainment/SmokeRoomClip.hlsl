// Draws one Ignis smoke particle and hides the part of it that falls outside
// the room it belongs to. Used by ContainedSmoke.shadergraph through a Custom
// Function node.
//
// Ignis smoke particles are large camera-facing quads, so one near a wall
// sticks through it and shows from the other side. Each particle is assigned
// to the room that holds its center, and any pixel of it outside that room
// fades out. Room boxes come from SmokeRoomClipVolumes at runtime and are
// shared by every contained smoke particle in the scene. With no rooms set,
// nothing is clipped.

#ifndef SMOKE_ROOM_CLIP_INCLUDED
#define SMOKE_ROOM_CLIP_INCLUDED

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

float SmokeRoomVisibility(float3 particleCenter, float3 worldPosition)
{
    int count = (int)_SmokeRoomCount;
    if (count <= 0)
        return 1.0;

    // The particle belongs to the room whose box holds its center, or the
    // nearest one when the center has drifted into a wall or a doorway.
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

    // The pixel shows if it is inside any box of that room.
    int first = (int)_SmokeRoomGroups[box].x;
    int boxes = (int)_SmokeRoomGroups[box].y;
    float fade = max(_SmokeRoomFade, 0.001);
    float visibility = 0.0;
    for (int j = 0; j < SMOKE_ROOM_CLIP_MAX_ROOMS; j++)
    {
        if (j >= boxes)
            break;

        visibility = max(visibility, saturate(SmokeRoomDepth(worldPosition, first + j) / fade));
    }

    return visibility;
}

// Same layout as VFX Graph's GetSubUV: frame 0 is the top-left tile.
float2 SmokeFlipbookUV(float frame, float2 uv, float2 size, float2 invSize)
{
    float2 tile = float2(fmod(frame, size.x), size.y - 1.0 - floor(frame * invSize.x));
    return (tile + uv) * invSize;
}

// Matches the OnlySmoke output it replaces: flipbook with frame blending,
// texture times particle color and alpha, then the room clip.
void ContainedSmokeParticle_float(
    UnityTexture2D MainTexture,
    float2 UV,
    float2 FlipbookSize,
    float TexIndex,
    float3 ParticleColor,
    float ParticleAlpha,
    float3 ParticleCenter,
    float3 WorldPosition,
    out float3 Color,
    out float Alpha)
{
    float2 size = max(FlipbookSize, float2(1.0, 1.0));
    float2 invSize = 1.0 / size;
    float blend = frac(TexIndex);
    float frame = TexIndex - blend;

    float4 current = SAMPLE_TEXTURE2D(MainTexture.tex, MainTexture.samplerstate, SmokeFlipbookUV(frame, UV, size, invSize));
    float4 next = SAMPLE_TEXTURE2D(MainTexture.tex, MainTexture.samplerstate, SmokeFlipbookUV(frame + 1.0, UV, size, invSize));
    float4 texel = lerp(current, next, blend);

    Color = ParticleColor * texel.rgb;
    Alpha = saturate(ParticleAlpha * texel.a * SmokeRoomVisibility(ParticleCenter, WorldPosition));
}

void ContainedSmokeParticle_half(
    UnityTexture2D MainTexture,
    float2 UV,
    float2 FlipbookSize,
    float TexIndex,
    float3 ParticleColor,
    float ParticleAlpha,
    float3 ParticleCenter,
    float3 WorldPosition,
    out half3 Color,
    out half Alpha)
{
    float3 color;
    float alpha;
    ContainedSmokeParticle_float(MainTexture, UV, FlipbookSize, TexIndex, ParticleColor, ParticleAlpha, ParticleCenter, WorldPosition, color, alpha);
    Color = color;
    Alpha = alpha;
}

#endif
