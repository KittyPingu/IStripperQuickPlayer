#version 120
uniform vec4 u_QuickPlayerMouse;

// Matches the 960x540 camera; the actor pivot is at its centre.
const float TARGET_DEPTH = 360.0;
const float FOCAL_LENGTH = 720.0;

void main()
{
    // Bridge XY is bottom-left normalized, Z = inside, W = valid (not buttons).
    vec2 target = u_QuickPlayerMouse.w > 0.5
        ? (clamp(u_QuickPlayerMouse.xy, 0.0, 1.0) - 0.5) * vec2(960.0, 540.0)
        : vec2(0.0);
    float yaw = atan(target.x, TARGET_DEPTH);
    float pitch = atan(target.y, length(vec2(target.x, TARGET_DEPTH)));

    vec4 position = gl_ModelViewProjectionMatrix * gl_Vertex;
    vec2 local = position.xy * vec2(480.0, 270.0);
    float cy = cos(yaw), sy = sin(yaw);
    float cp = cos(pitch), sp = sin(pitch);
    // Rotate the plane's normal toward (target.x, target.y, TARGET_DEPTH).
    position.x = (local.x * cy - local.y * sp * sy) / 480.0;
    position.y = local.y * cp / 270.0;
    position.w += (local.x * sy + local.y * sp * cy) / FOCAL_LENGTH;
    gl_Position = position;
    gl_TexCoord[0] = gl_MultiTexCoord0;
    gl_FrontColor = gl_Color;
}
