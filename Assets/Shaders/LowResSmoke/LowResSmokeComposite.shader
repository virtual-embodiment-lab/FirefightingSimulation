// Lays the low-resolution smoke (LowResSmokeFeature) over the frame. The
// smoke texture holds the smoke's color, already multiplied by its opacity,
// and its opacity, so it goes over the frame the way the smoke's own cards
// would have. The scene in front of the smoke was already kept out of it, by
// the depth it was drawn against.
Shader "Hidden/LowResSmoke/Composite"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Composite"
            Cull Off
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D_X(_LowResSmokeTexture);
            // x, y: 1 / width and 1 / height of the frame being drawn into.
            float4 _LowResSmokeFrameTexel;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
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
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                // The smoke was drawn with the frame's own projection, so a pixel
                // of the frame and the same place in the smoke line up in
                // framebuffer space, however either one is flipped.
                float2 uv = input.positionCS.xy * _LowResSmokeFrameTexel.xy;
                return SAMPLE_TEXTURE2D_X(_LowResSmokeTexture, sampler_LinearClamp, uv);
            }
            ENDHLSL
        }
    }
}
