Shader "Nanite/DebugClusters"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            Name "ForwardLit"
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
            int _DebugColorMode;
            float _LodErrorMax;
            float _LodColorBands;
            int _MaxLodLevel;

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                uint instanceID : SV_InstanceID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 color : TEXCOORD0;
            };

            float3 Hash31(uint i)
            {
                float3 p = frac(float3(i * 0.1031, i * 0.1030, i * 0.0973));
                p += dot(p, p.yxz + 33.33);
                return frac((p.xxy + p.yzz) * p.zyx);
            }

            // Discrete LOD level palette — each level gets a visually distinct color
            static const float3 LOD_PALETTE[8] = {
                float3(0.10, 0.30, 1.00),  // LOD 0 — blue
                float3(0.00, 0.80, 1.00),  // LOD 1 — cyan
                float3(0.00, 0.90, 0.35),  // LOD 2 — green
                float3(0.70, 0.95, 0.10),  // LOD 3 — lime
                float3(1.00, 0.80, 0.00),  // LOD 4 — yellow
                float3(1.00, 0.45, 0.00),  // LOD 5 — orange
                float3(1.00, 0.15, 0.10),  // LOD 6 — red
                float3(0.80, 0.10, 0.80),  // LOD 7+ — magenta
            };

            float3 LODLevelColor(int lodLevel)
            {
                return LOD_PALETTE[clamp(lodLevel, 0, 7)];
            }

            float3 LODBandColor(float lodError)
            {
                float t = saturate(lodError / max(_LodErrorMax, 1e-5));
                float bandCount = max(2.0, _LodColorBands);
                float denom = max(1.0, bandCount - 1.0);
                float bandT = floor(t * denom + 0.5) / denom;

                float3 c0 = float3(0.15, 0.30, 1.00);
                float3 c1 = float3(0.00, 0.85, 1.00);
                float3 c2 = float3(0.00, 1.00, 0.35);
                float3 c3 = float3(1.00, 0.85, 0.10);
                float3 c4 = float3(1.00, 0.20, 0.10);

                float x = bandT * 4.0;
                if (x < 1.0) return lerp(c0, c1, x);
                if (x < 2.0) return lerp(c1, c2, x - 1.0);
                if (x < 3.0) return lerp(c2, c3, x - 2.0);
                return lerp(c3, c4, min(1.0, x - 3.0));
            }

            Varyings vert (Attributes input)
            {
                Varyings output;

                uint clusterID = _VisibleClusterBuffer[input.instanceID];
                NaniteCluster cluster = _ClusterBuffer[clusterID];

                if (input.vertexID >= (uint)cluster.indexCount)
                {
                    output.positionCS = float4(0, 0, 0, 1);
                    output.color = float3(0, 0, 0);
                    return output;
                }

                int indexPos = cluster.indexStart + input.vertexID;
                int vertexIndex = _IndexBuffer[indexPos];
                NaniteVertex v = _VertexBuffer[vertexIndex];

                float3 positionWS = mul(_NaniteObjectToWorld, float4(v.position, 1.0)).xyz;
                output.positionCS = TransformWorldToHClipCompat(positionWS);

                // _DebugColorMode:
                //   0 = Cluster ID hash (random per cluster)
                //   1 = LOD level (discrete palette)
                //   2 = LOD error gradient
                if (_DebugColorMode == 1)
                    output.color = LODLevelColor(cluster.lodLevel);
                else if (_DebugColorMode == 2)
                    output.color = LODBandColor(cluster.lodError);
                else
                    output.color = Hash31(clusterID);

                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                return half4(input.color, 1.0);
            }
            ENDHLSL
        }
    }
}
