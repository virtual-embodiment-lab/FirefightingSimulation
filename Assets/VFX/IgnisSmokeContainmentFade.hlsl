// Run after Ignis applies its size and opacity curves so this only changes rendered opacity.
void FadeSmokeNearWalls(inout VFXAttributes attributes, in VFXSampler3D ContainmentField,
    in float3 ContainmentMinimum, in float3 ContainmentSize, in float ContainmentVoxel)
{
    if (ContainmentVoxel <= 0.0)
        return;

    float3 uv = (attributes.position - ContainmentMinimum) / ContainmentSize;
    if (any(uv < 0.0) || any(uv > 1.0))
        return;

    float4 surface = SampleTexture(ContainmentField, uv, 0);
    float clearance = ContainmentVoxel * 0.9;
    // Larger sprites can overlap a wall farther away. Stay within the field's 0.8 m distance band.
    float fadeEnd = min(0.75, max(clearance + ContainmentVoxel, abs(attributes.size) * 0.5));
    float wallFade = smoothstep(clearance, fadeEnd, surface.w);
    // Fade against upright surfaces while retaining floor and ceiling density.
    float up = abs(surface.y) / max(length(surface.xyz), 0.001);
    float wallWeight = 1.0 - smoothstep(0.25, 0.65, up);
    attributes.alpha *= lerp(1.0, wallFade, wallWeight);
}
