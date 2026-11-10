// Composites a camera's render texture (UI drawn over transparent black) on
// top of another layer.
//
// uGUI blends alpha with SrcAlpha too, so over a cleared target a pixel of
// alpha a ends up stored as a*a while its colour is already premultiplied.
// sqrt() recovers a for a single layer (exact) and is close for stacked ones;
// then it is a plain premultiplied "over".

Shader "TimelineVR/UICompositeLayer"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "CanUseSpriteAtlas"="False" }
        Cull Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv);
                float a = sqrt(saturate(c.a));
                // Graphic colour / CanvasGroup alpha fade the whole layer.
                return fixed4(c.rgb, a) * i.color.a;
            }
            ENDCG
        }
    }
}
