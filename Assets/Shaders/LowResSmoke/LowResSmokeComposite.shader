// Lays the low-resolution flames or smoke (LowResSmokeFeature) over the frame.
// The scene in front of them was already kept out, by the depth they were
// drawn against.
//
// It draws on a full-screen triangle (LowResSmokeEffects) whose vertices are
// already in clip space, in URP's own transparent pass. A pass of its own
// after that one split the frame's render pass on the Quest: the whole
// multisampled frame went out to memory and back, about 5 ms.
//
// Flames material: _Source 1, Blend One One, Zero One. The flame texture holds
//   the light the flames add; added to the frame first, it saturates there
//   the way the flames did when they were drawn into the frame.
// Smoke material: _Source 0, Blend One OneMinusSrcAlpha. The smoke texture
//   holds the smoke's color, already multiplied by its opacity, and its
//   opacity, so it goes over the frame the way the smoke's own cards would.
Shader "Hidden/LowResSmoke/Composite"
{
    Properties
    {
        _Source ("Source (0 smoke, 1 flames)", Float) = 0
        _SrcBlend ("Src Blend", Float) = 1
        _DstBlend ("Dst Blend", Float) = 10
        _SrcBlendAlpha ("Src Blend Alpha", Float) = 1
        _DstBlendAlpha ("Dst Blend Alpha", Float) = 10
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }

        Pass
        {
            Name "Composite"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Off
            ZWrite Off
            ZTest Always
            Blend [_SrcBlend] [_DstBlend], [_SrcBlendAlpha] [_DstBlendAlpha]

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X(_LowResSmokeTexture);
            TEXTURE2D_X(_LowResFlamesTexture);
            float _Source;

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = float4(input.positionOS.xy, UNITY_NEAR_CLIP_VALUE, 1.0);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                // The texture was drawn with the frame's own projection, so a
                // pixel of the frame and the same place in the texture line up
                // in framebuffer space, however either one is flipped.
                float2 uv = input.positionCS.xy * (_ScaledScreenParams.zw - 1.0);
                return _Source > 0.5
                    ? SAMPLE_TEXTURE2D_X(_LowResFlamesTexture, sampler_LinearClamp, uv)
                    : SAMPLE_TEXTURE2D_X(_LowResSmokeTexture, sampler_LinearClamp, uv);
            }
            ENDHLSL
        }
    }
}
