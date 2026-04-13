Shader "Nanite/Shaded"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.72, 0.68, 0.62, 1)
        _MainTex ("Albedo", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            Tags { "LightMode" = "Always" }

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            float4x4 unity_MatrixVP;

            float4 TransformWorldToHClipCompat(float3 positionWS)
            {
                return mul(unity_MatrixVP, float4(positionWS, 1.0));
            }

            struct NaniteVertex
            {
                float3 position;
                float3 normal;
                float2 uv;
            };

            struct NaniteCluster
            {
                int indexStart;
                int indexCount;
                float3 boundsCenter;
                float boundsRadius;
                float lodError;
                float parentLodError;
                float3 lodBoundsCenter;
                float lodBoundsRadius;
                float3 parentBoundsCenter;
                float parentBoundsRadius;
                int lodLevel;
            };

            StructuredBuffer<NaniteVertex> _VertexBuffer;
            StructuredBuffer<int> _IndexBuffer;
            StructuredBuffer<NaniteCluster> _ClusterBuffer;
            StructuredBuffer<uint> _VisibleClusterBuffer;

            float4x4 _NaniteObjectToWorld;
            float3 _NaniteLightDirWS;
            float4 _BaseColor;
            sampler2D _MainTex;
            float4 _MainTex_ST;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
            };

            Varyings vert (Attributes input)
            {
                Varyings output;

                uint clusterID = _VisibleClusterBuffer[input.instanceID];
                NaniteCluster cluster = _ClusterBuffer[clusterID];

                if (input.vertexID >= (uint)cluster.indexCount)
                {
                    output.positionCS = float4(0, 0, 0, 1);
                    output.normalWS = float3(0, 1, 0);
                    output.uv = float2(0, 0);
                    return output;
                }

                int indexPos = cluster.indexStart + input.vertexID;
                int vertexIndex = _IndexBuffer[indexPos];
                NaniteVertex v = _VertexBuffer[vertexIndex];

                float3 positionWS = mul(_NaniteObjectToWorld, float4(v.position, 1.0)).xyz;
                output.positionCS = TransformWorldToHClipCompat(positionWS);
                output.normalWS = normalize(mul((float3x3)_NaniteObjectToWorld, v.normal));
                output.uv = v.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                float3 L = normalize(_NaniteLightDirWS);
                float ndl = saturate(dot(normalize(input.normalWS), L));
                half3 albedo = (half3)_BaseColor.rgb * (half3)tex2D(_MainTex, input.uv).rgb;
                half3 ambient = (half3)(albedo * 0.2);
                half3 lit = (half3)(albedo * (0.2 + 0.8 * ndl));
                return half4(lit, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
