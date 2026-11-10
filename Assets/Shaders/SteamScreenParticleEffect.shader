Shader "Clocktower/Unlit/SteamScreenParticleEffect"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _NoiseTex ("Noise", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Blend SrcAlpha One
        ZWrite Off

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            // make fog work
            #pragma multi_compile_fog

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 uv : TEXCOORD0;
                float4 uv2 : TEXCOORD1;
                float4 color : COLOR;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            sampler2D _SubjectTex;
            float2 _SubjectSize;
            float _SubjectFade;

            float4x4 _WorldToObject;

            sampler2D _NoiseTex;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv.xy = TRANSFORM_TEX(v.uv, _MainTex);
                o.color = v.color;

                float3 localPos = mul(_WorldToObject, v.vertex).xyz;

                float2 uv2;
                uv2.x = localPos.x / _SubjectSize.x;
                uv2.y = localPos.y / _SubjectSize.y;

                o.uv.zw = uv2;
                o.uv.w *= 0.0001f;
                o.uv.zw += 0.5;

                uv2.x *= lerp(0.01, 1.0f, pow(_SubjectFade, 10.0));
                uv2.y *= lerp(0.01, 1.0f, pow(_SubjectFade, 200.0));
                uv2.xy += 0.5;

                o.uv2.xy = uv2;

                o.uv2.z = pow(_SubjectFade, 4.0);
                o.uv2.w = 0.0f;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float fade = _SubjectFade;
                float fade4 = i.uv2.z;
                fixed4 col = tex2D(_MainTex, i.uv.xy);// * tex2D(_SubjectTex, i.uv.zw);
                fixed4 subjcol = tex2D(_SubjectTex, i.uv2.xy) * col.a * 0.2;

                return lerp(col, subjcol, fade) * lerp(i.color, 1, fade4);
                //return col * i.color * lerp(1.0, 0.5, pow(fade, 4.0)) + subjcol * fade;
            }
            ENDCG
        }
    }
}
