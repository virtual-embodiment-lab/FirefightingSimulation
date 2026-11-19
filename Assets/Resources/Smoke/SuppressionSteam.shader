Shader "Firefighting/Suppression Steam"
{
    Properties
    {
        [HideInInspector] _SteamRoomBox("Steam Room Box", Float) = -1
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        Cull Off
        ZWrite Off

        Pass
        {
            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex SteamVertex
            #pragma fragment SteamFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl"
            #include "Assets/Shaders/SmokeContainment/SmokeRoomClip.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _SteamRoomBox;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                half4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                half4 color : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings SteamVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.uv = input.uv;
                output.color = input.color;
                return output;
            }

            half4 SteamFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 p = input.uv * 2.0 - 1.0;
                // A few soft overlapping lobes give each card an uneven vapor edge.
                float center = saturate(1.0 - dot(p, p) * 1.65);
                float2 left = p - float2(-0.28, 0.19);
                float2 right = p - float2(0.27, -0.16);
                float lobes = saturate(1.0 - dot(left, left) * 2.35)
                            + saturate(1.0 - dot(right, right) * 2.35);
                float edge = saturate((1.0 - max(abs(p.x), abs(p.y))) * 5.0);
                float density = saturate(center * 0.64 + lobes * 0.31);
                float visibility = SmokeRoomVisibility(_SteamRoomBox, input.positionWS);
                return half4(input.color.rgb, input.color.a * density * density * edge * visibility);
            }
            ENDHLSL
        }
    }
}
