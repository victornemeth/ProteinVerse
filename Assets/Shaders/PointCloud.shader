Shader "Custom/PointCloud"
{
    Properties
    {
        _PointSize  ("Point Size (world units)", Float) = 0.012
        _PointCount ("Point Count",              Int)   = 0
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" "IgnoreProjector"="True" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 4.5
            #pragma multi_compile __ STEREO_MULTIVIEW_ON UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON

            #include "UnityCG.cginc"

            StructuredBuffer<float3> _Positions;
            StructuredBuffer<float4> _Colors;

            float4x4 _CloudMatrix;
            float    _PointSize;
            uint     _PointCount;   // used to wrap instanceID for single-pass stereo instanced mode

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                float4 color : COLOR0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                v2f o;
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // In single-pass stereo instanced mode Unity doubles the instance count
                // (0..N-1 = left eye, N..2N-1 = right eye). Modulo maps both to 0..N-1.
                uint idx = (_PointCount > 0) ? (instanceID % _PointCount) : instanceID;

                float3 localPos = _Positions[idx];
                float4 worldPos = mul(_CloudMatrix, float4(localPos, 1.0));

                float4 viewPos = mul(UNITY_MATRIX_V, worldPos);
                viewPos.xy   += v.vertex.xy * _PointSize;

                o.pos   = mul(UNITY_MATRIX_P, viewPos);
                o.uv    = v.uv;
                o.color = _Colors[idx];
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv   = i.uv * 2.0 - 1.0;
                float  dist = dot(uv, uv);
                if (dist > 1.0) discard;
                float alpha = 1.0 - smoothstep(0.55, 1.0, dist);
                return float4(i.color.rgb, alpha * i.color.a);
            }
            ENDCG
        }
    }
}
