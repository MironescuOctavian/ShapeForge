// Development only. Flat-lit vertex color for ReferenceBench renders.
Shader "Hidden/ShapeForgeBenchHeat"
{
    Properties { _Color ("Color", Color) = (1,1,1,1) }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Color;
            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float3 normal : TEXCOORD0; fixed4 color : COLOR; };
            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.color = v.color * _Color;
                return o;
            }
            fixed4 frag (v2f i, fixed facing : VFACE) : SV_Target
            {
                float3 n = normalize(i.normal) * (facing > 0 ? 1 : -1);
                float3 l = normalize(float3(0.3, 0.6, 1));
                float3 v = normalize(UNITY_MATRIX_V[2].xyz);
                float shade = 0.3 + 0.55 * saturate(dot(n, l)) + 0.25 * saturate(dot(n, v));
                return fixed4(i.color.rgb * shade, 1);
            }
            ENDCG
        }
    }
}
