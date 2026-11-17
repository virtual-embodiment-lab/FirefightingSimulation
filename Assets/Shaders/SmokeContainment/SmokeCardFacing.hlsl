// Turns each smoke card toward the viewer without following the head's rotation.
//
// A smoke particle is a flat card. VFX Graph's Orient block takes the card's up
// direction from the camera. In a headset that is the head, so pitching or
// rolling the head pitches or rolls every card with it. The smoke seems to
// move with the head, and while the layer is still forming under the ceiling,
// looking up swings the cards out from in front of the fire.
//
// Here the card faces the point between the eyes, and its up is the world's
// up, the way Unity's particle system draws with Allow Roll off for VR. Turning,
// pitching or rolling the head leaves every card where it is; only moving the
// head turns them. The particle's random angle is applied on top as before.
//
// The VFX outputs call SmokeCardAxes from a Custom HLSL block that replaces
// their Orient block (ContainedSmokeBuilder.FaceHeadUpright).

#ifndef SMOKE_CARD_FACING_INCLUDED
#define SMOKE_CARD_FACING_INCLUDED

// Where the cards face, in the effect's space. In the headset this is between
// the eyes, so both eyes see the same card. With only one eye's camera, each
// eye would see a card straight overhead rolled differently. Any other camera,
// such as the Scene view or the thermal camera, uses its own position.
float3 SmokeViewerPosition()
{
#if defined(USING_STEREO_MATRICES)
    float3 head = 0.5 * (unity_StereoWorldSpaceCameraPos[0] + unity_StereoWorldSpaceCameraPos[1]);
    #ifdef VFX_WORLD_SPACE
    return head;
    #else
    return mul(VFXGetWorldToObjectMatrix(), float4(head, 1.0)).xyz;
    #endif
#else
    return GetViewVFXPosition();
#endif
}

// The world's up direction, in the effect's space.
float3 SmokeWorldUp()
{
#ifdef VFX_WORLD_SPACE
    return float3(0.0, 1.0, 0.0);
#else
    return VFXSafeNormalize(mul((float3x3)VFXGetWorldToObjectMatrix(), float3(0.0, 1.0, 0.0)));
#endif
}

// The Quest's budget for smoke right around the viewer, set as a global by
// QuestPerformanceSettings; all zero (off) anywhere else. A smoke card is
// meters wide, so every card within a few meters of the eyes covers the whole
// view, and inside a smoke layer dozens of them stack up on every pixel.
//   x: a card is drawn at most this many times as wide as its distance from
//      the viewer, so a close card covers a bounded part of the view. 0 = off.
//   y: cards closer than this are not drawn at all. 0 = off.
float4 _SmokeNearBudget;

void SmokeNearBudget(float3 position, inout float size, inout float alpha)
{
    if (_SmokeNearBudget.x <= 0.0 && _SmokeNearBudget.y <= 0.0)
        return;

    float away = length(position - SmokeViewerPosition());
    if (_SmokeNearBudget.x > 0.0)
        size = min(size, _SmokeNearBudget.x * away);
    if (away < _SmokeNearBudget.y)
    {
        size = 0.0;
        alpha = 0.0;
    }
}

// Card axes for a particle at position: Z points from the viewer to the card,
// X runs level with the floor, and Y is as close to the world's up as a card
// facing the viewer can be. Only a card almost straight above or below the
// viewer has no level direction, and turns as the viewer moves under or over it.
void SmokeCardAxes(float3 position, out float3 axisX, out float3 axisY, out float3 axisZ)
{
    axisZ = VFXSafeNormalize(position - SmokeViewerPosition());
    axisX = VFXSafeNormalizedCross(SmokeWorldUp(), axisZ, float3(1.0, 0.0, 0.0));
    axisY = cross(axisZ, axisX);
}

#endif
