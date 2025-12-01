#version 450

layout(binding = 0) uniform sampler2D _MainTex;
layout(location = 0) in  vec2 vs_TEXCOORD0;
layout(location = 0) out vec4 SV_Target0;

vec4 u_xlat0;
bvec2 u_xlatb0;
vec4 u_xlat1;
bvec2 u_xlatb4;

void main()
{
    u_xlatb0.xy = greaterThanEqual(vs_TEXCOORD0.xyxx, vec4(0.0, 0.0, 0.0, 0.0)).xy;
    u_xlatb4.xy = greaterThanEqual(vec4(1.0, 1.0, 1.0, 1.0), vs_TEXCOORD0.xyxy).xy;
    u_xlatb0.x = u_xlatb4.x && u_xlatb0.x;
    u_xlatb0.x = u_xlatb0.y && u_xlatb0.x;
    u_xlatb0.x = u_xlatb4.y && u_xlatb0.x;
    u_xlat1 = texture(_MainTex, vs_TEXCOORD0.xy);
    u_xlat0 = u_xlatb0.x ? u_xlat1 : vec4(0.0, 0.0, 0.0, 0.0);
    SV_Target0 = u_xlat0;
    return;
}
