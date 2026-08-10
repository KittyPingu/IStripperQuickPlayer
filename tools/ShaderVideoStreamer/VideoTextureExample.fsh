uniform sampler2D texture0;

uniform sampler2D u_QuickPlayerTexture_video1;
uniform vec2 u_QuickPlayerTextureSize_video1;
uniform float u_QuickPlayerTextureSequence_video1;
uniform vec4 u_QuickPlayerData;
uniform vec2 u_WindowSize;

void main(void)
{
    vec2 uv = gl_TexCoord[0].xy;
    vec4 scene = texture2D(texture0, uv) * gl_Color;

    if (u_QuickPlayerTextureSize_video1.x <= 0.0)
    {
        gl_FragColor = scene;
        return;
    }

    vec2 panelPixels = max(vec2(
        u_QuickPlayerData.z, u_QuickPlayerData.a), vec2(1.0));
    vec2 panelSize = panelPixels / u_WindowSize;
    vec2 panelCentre = clamp(u_QuickPlayerData.xy,
        panelSize * 0.5, vec2(1.0) - panelSize * 0.5);
    vec2 videoUv = (uv - (panelCentre - panelSize * 0.5)) / panelSize;
    if (videoUv.x < 0.0 || videoUv.x > 1.0 ||
        videoUv.y < 0.0 || videoUv.y > 1.0)
    {
        gl_FragColor = scene;
        return;
    }

    // QuickPlayer accepts top-left-origin frames and presents an OpenGL
    // texture, so flip Y when the scene's UV origin is at the top-left.
    videoUv.y = 1.0 - videoUv.y;
    vec4 video = texture2D(u_QuickPlayerTexture_video1, videoUv);
    gl_FragColor = mix(scene, video, video.a);
}
