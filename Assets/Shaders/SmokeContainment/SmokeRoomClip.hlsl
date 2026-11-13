// Draws one Ignis smoke particle and hides the part of it that falls outside
// the room it belongs to. Used by ContainedSmoke.shadergraph through a Custom
// Function node.
//
// Ignis smoke particles are large camera-facing quads, so one near a wall
// sticks through it and shows from the other side. Each particle is assigned
// to the room that holds its center, and any pixel of it outside that room
// fades out. Which room a particle belongs to is worked out once per particle
// corner in SmokeRoomSelect.hlsl and arrives here as Room, the index of one of
// that room's boxes; this file only tests the pixel against that room's boxes.
// A Room below zero is not clipped.
//
// ContainedFlame.shadergraph uses the same rooms for the Flames and Embers of
// Flame_box_wild_contained. Those particles belong to the room of the object
// that is burning (see FlameOwnerRoom).

#ifndef SMOKE_ROOM_CLIP_INCLUDED
#define SMOKE_ROOM_CLIP_INCLUDED

#include "Assets/Shaders/SmokeContainment/SmokeRoomSelect.hlsl"

// The pixel shows if it is inside any box of the room that box belongs to.
float SmokeRoomGroupVisibility(int box, float3 worldPosition)
{
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

// Visibility of a pixel of a particle that belongs to the room of box Room.
float SmokeRoomVisibility(float room, float3 worldPosition)
{
    int count = (int)_SmokeRoomCount;
    int box = (int)(room + 0.5);
    if (room < 0.0 || count <= 0 || box >= count)
        return 1.0;

    return SmokeRoomGroupVisibility(box, worldPosition);
}

// Same layout as VFX Graph's GetSubUV: frame 0 is the top-left tile.
float2 SmokeFlipbookUV(float frame, float2 uv, float2 size, float2 invSize)
{
    float2 tile = float2(fmod(frame, size.x), size.y - 1.0 - floor(frame * invSize.x));
    return (tile + uv) * invSize;
}

// Flipbook sample with frame blending, like VFX Graph's flipbook outputs. A
// texture without frames uses a FlipbookSize of 1 and a TexIndex of 0.
float4 SmokeFlipbookSample(UnityTexture2D MainTexture, float2 UV, float2 FlipbookSize, float TexIndex)
{
    float2 size = max(FlipbookSize, float2(1.0, 1.0));
    float2 invSize = 1.0 / size;
    float blend = frac(TexIndex);
    float frame = TexIndex - blend;

    float4 current = SAMPLE_TEXTURE2D(MainTexture.tex, MainTexture.samplerstate, SmokeFlipbookUV(frame, UV, size, invSize));
    float4 next = SAMPLE_TEXTURE2D(MainTexture.tex, MainTexture.samplerstate, SmokeFlipbookUV(frame + 1.0, UV, size, invSize));
    return lerp(current, next, blend);
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
    float Room,
    float3 WorldPosition,
    out float3 Color,
    out float Alpha)
{
    float4 texel = SmokeFlipbookSample(MainTexture, UV, FlipbookSize, TexIndex);
    Color = ParticleColor * texel.rgb;
    Alpha = saturate(ParticleAlpha * texel.a * SmokeRoomVisibility(Room, WorldPosition));
}

void ContainedSmokeParticle_half(
    UnityTexture2D MainTexture,
    float2 UV,
    float2 FlipbookSize,
    float TexIndex,
    float3 ParticleColor,
    float ParticleAlpha,
    float Room,
    float3 WorldPosition,
    out half3 Color,
    out half Alpha)
{
    float3 color;
    float alpha;
    ContainedSmokeParticle_float(MainTexture, UV, FlipbookSize, TexIndex, ParticleColor, ParticleAlpha, Room, WorldPosition, color, alpha);
    Color = color;
    Alpha = alpha;
}

// Matches the additive Flames and Embers outputs of Flame_box_wild: texture
// times particle color and alpha, with no clamp, then the room clip around the
// burning object, whose room arrives as Room.
void ContainedFlameParticle_float(
    UnityTexture2D MainTexture,
    float2 UV,
    float2 FlipbookSize,
    float TexIndex,
    float3 ParticleColor,
    float ParticleAlpha,
    float Room,
    float3 WorldPosition,
    out float3 Color,
    out float Alpha)
{
    float4 texel = SmokeFlipbookSample(MainTexture, UV, FlipbookSize, TexIndex);
    Color = ParticleColor * texel.rgb;
    Alpha = ParticleAlpha * texel.a * SmokeRoomVisibility(Room, WorldPosition);
}

void ContainedFlameParticle_half(
    UnityTexture2D MainTexture,
    float2 UV,
    float2 FlipbookSize,
    float TexIndex,
    float3 ParticleColor,
    float ParticleAlpha,
    float Room,
    float3 WorldPosition,
    out half3 Color,
    out half Alpha)
{
    float3 color;
    float alpha;
    ContainedFlameParticle_float(MainTexture, UV, FlipbookSize, TexIndex, ParticleColor, ParticleAlpha, Room, WorldPosition, color, alpha);
    Color = color;
    Alpha = alpha;
}

#endif
