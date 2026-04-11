Shader "Custom/PointCloud"
{
    Properties
    {
        _PointSize ("Point Size (world units)", Float) = 0.012
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        ZWrite On
        ZTest LEqual
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile __ STEREO_MULTIVIEW_ON UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON

            #include "UnityCG.cginc"

            float _PointSize;

            struct appdata
            {
                float4 vertex : POSITION;   // xyz = cloud-local centre (same for all 4 quad verts)
                float2 uv     : TEXCOORD0;  // quad corner in [0,1]²
                float4 color  : COLOR;      // viridis colour baked at build time
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                half2  uv    : TEXCOORD0;
                half4  color : COLOR0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // UnityObjectToClipPos uses UNITY_MATRIX_MVP which Unity injects as the
                // correct per-eye MVP in every stereo mode (multiview, SPI, multi-pass).
                // No manual UNITY_MATRIX_V / UNITY_MATRIX_VP juggling needed.
                float4 clipPos = UnityObjectToClipPos(v.vertex);

                // Billboard expansion in clip space.
                // corner remaps UV [0,1] → [-0.5, 0.5] for the four quad vertices.
                // P._m00 / P._m11 convert the view-space size into clip-space pixels.
                float2 corner = v.uv - 0.5;
                clipPos.xy += corner * _PointSize
                            * float2(UNITY_MATRIX_P._m00, UNITY_MATRIX_P._m11);

                o.pos   = clipPos;
                o.uv    = (half2)v.uv;
                o.color = (half4)v.color;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                // Map UV [0,1] → [-1,1]; dist² = 0 at centre, 1 at disc edge.
                half2 uv   = i.uv * 2.0h - 1.0h;
                half  dist = dot(uv, uv);

                // Discard the quad corners outside the circle.
                clip(0.99h - dist);

                // Dark-blue edge ring — helps separate overlapping points
                // and gives depth cues.
                half  edge    = smoothstep(0.62h, 0.92h, dist);
                half4 edgeCol = half4(0.0h, 0.03h, 0.12h, 1.0h);
                return lerp(i.color, edgeCol, edge * 0.85h);
            }
            ENDCG
        }
    }
}
