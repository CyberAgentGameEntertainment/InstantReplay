
#version 450

layout(push_constant) uniform PushConstants {
    vec4 _ScaleAndTiling;
};

layout(location = 0) out highp vec2 vs_TEXCOORD0;

void main()
{
    if(gl_VertexIndex == 0) {
        gl_Position = vec4(-1.0, 1.0, 0.0, 1.0);
        vs_TEXCOORD0 = vec2(0.0, 0.0) * _ScaleAndTiling.xy + _ScaleAndTiling.zw;
    } else if(gl_VertexIndex == 1) {
        gl_Position = vec4(3.0, 1.0, 0.0, 1.0);
        vs_TEXCOORD0 = vec2(2.0, 0.0) * _ScaleAndTiling.xy + _ScaleAndTiling.zw;
    } else if(gl_VertexIndex == 2) {
        gl_Position = vec4(-1.0, -3.0, 0.0, 1.0);
        vs_TEXCOORD0 = vec2(0.0, 2.0) * _ScaleAndTiling.xy + _ScaleAndTiling.zw;
    }
}
