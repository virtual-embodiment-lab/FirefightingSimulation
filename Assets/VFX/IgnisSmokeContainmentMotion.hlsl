// Replace Euler translation, using the velocity produced by Ignis's preceding update blocks.
void ContainSmokeMotion(inout VFXAttributes attributes, in VFXSampler3D ContainmentField,
    in float3 ContainmentMinimum, in float3 ContainmentSize, in float ContainmentVoxel,
    in float StepSeconds)
{
    float3 position = attributes.position;
    float3 velocity = attributes.velocity;
    if (ContainmentVoxel <= 0.0)
    {
        attributes.position = position + velocity * StepSeconds;
        return;
    }

    float spacing = ContainmentVoxel * 0.5;
    float clearance = ContainmentVoxel * 0.9;
    float distance = length(velocity * StepSeconds);
    int steps = clamp((int)ceil(distance / spacing), 1, 12);
    float stepTime = min(StepSeconds / float(steps), spacing / max(length(velocity), 1e-5));
    for (int step = 0; step < steps; ++step)
    {
        float3 next = position + velocity * stepTime;
        float3 uv = (next - ContainmentMinimum) / ContainmentSize;
        if (all(uv >= 0.0) && all(uv <= 1.0))
        {
            float4 contact = SampleTexture(ContainmentField, uv, 0);
            if (contact.w < clearance)
            {
                float3 previousUV = (position - ContainmentMinimum) / ContainmentSize;
                if (all(previousUV >= 0.0) && all(previousUV <= 1.0))
                {
                    float4 previous = SampleTexture(ContainmentField, previousUV, 0);
                    if (previous.w < clearance + spacing && dot(previous.xyz, contact.xyz) < 0.0)
                        contact.xyz = previous.xyz;
                }
                if (dot(contact.xyz, contact.xyz) > 0.01)
                {
                    float3 normal = normalize(contact.xyz);
                    velocity -= normal * min(0.0, dot(velocity, normal));
                    // Check the slide from the last clear position, including on a one-step frame.
                    float3 slide = position + velocity * stepTime;
                    float3 slideUV = (slide - ContainmentMinimum) / ContainmentSize;
                    if (any(slideUV < 0.0) || any(slideUV > 1.0) ||
                        SampleTexture(ContainmentField, slideUV, 0).w >= clearance)
                        position = slide;
                    continue;
                }
                velocity = 0.0;
                break;
            }
        }
        position = next;
    }

    attributes.position = position;
    attributes.velocity = velocity;
}
