// Reject samples on the far side of a surface, even if the destination itself is open.
void ContainSmokeBirth(inout VFXAttributes attributes, in VFXSampler3D ContainmentField,
    in float3 ContainmentMinimum, in float3 ContainmentSize, in float ContainmentVoxel,
    in float3 EmitterCenter)
{
    if (ContainmentVoxel <= 0.0)
        return;

    float3 offset = attributes.position - EmitterCenter;
    float sampleSpacing = ContainmentVoxel * 0.5;
    int samples = max(1, (int)ceil(length(offset) / sampleSpacing));
    if (samples > 64)
    {
        attributes.alive = false;
        return;
    }

    for (int i = 0; i <= samples; ++i)
    {
        float3 samplePosition = EmitterCenter + offset * (float(i) / float(samples));
        float3 uv = (samplePosition - ContainmentMinimum) / ContainmentSize;
        if (all(uv >= 0.0) && all(uv <= 1.0) &&
            SampleTexture(ContainmentField, uv, 0).w < ContainmentVoxel * 0.9)
        {
            attributes.alive = false;
            return;
        }
    }
}
