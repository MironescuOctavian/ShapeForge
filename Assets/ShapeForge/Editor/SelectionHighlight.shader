Shader "Hidden/ShapeForge/SelectionHighlight"
{
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Offset -1, -1

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 vert(float4 position : POSITION) : SV_POSITION
            {
                return UnityObjectToClipPos(position);
            }

            fixed4 frag() : SV_Target
            {
                return fixed4(1.0, 0.72, 0.23, 0.44);
            }
            ENDCG
        }
    }
}
