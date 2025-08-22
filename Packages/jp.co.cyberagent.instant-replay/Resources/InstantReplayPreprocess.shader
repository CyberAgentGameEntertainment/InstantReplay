Shader "Hidden/InstantReplay/Rechannel"
{
    Properties
    {
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _ScaleAndTiling;
            float4 _MainTex_ST;
            float4x4 _Rechannel;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv * _ScaleAndTiling.xy + _ScaleAndTiling.zw; // TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = i.uv.x >= 0.0 && i.uv.x <= 1.0 && i.uv.y >= 0.0 && i.uv.y <= 1.0 ? tex2D(_MainTex, i.uv) : (0.0).xxxx;
                return mul(_Rechannel, col);
            }
            ENDCG
        }
    }
}
