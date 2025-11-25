Shader "Unlit/FlyInstanced"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        LOD 100

        Pass
        {
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Off
            ZWrite On
            Blend Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma instancing_options procedural:setup

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata
            {
                float4 positionOS : POSITION;
                uint instanceID : SV_InstanceID;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
            };

            struct FlyData
            {
                float3 position;
                float3 velocity;
                float4x4 mat;
                int state;
            };

            #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
            StructuredBuffer<FlyData> boidDataBuffer;
            #endif

            half4 _Color;

            void setup()
            {
                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                #endif
            }

            v2f vert (appdata v)
            {
                v2f o;

                #ifdef UNITY_PROCEDURAL_INSTANCING_ENABLED
                    FlyData data = boidDataBuffer[v.instanceID];
                    float4 worldPos = mul(data.mat, v.positionOS);
                    o.vertex = TransformWorldToHClip(worldPos.xyz);
                #else
                    o.vertex = TransformObjectToHClip(v.positionOS.xyz);
                #endif

                return o;
            }

            half4 frag (v2f i) : SV_Target
            {
                return _Color;
            }
            ENDHLSL
        }
    }
}
