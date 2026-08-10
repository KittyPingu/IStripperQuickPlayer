uniform sampler2D texture0;

uniform sampler2D u_QuickPlayerTexture_video1;
uniform vec2 u_QuickPlayerTextureSize_video1;
uniform float u_QuickPlayerTextureSequence_video1;
uniform vec4 u_QuickPlayerData;
uniform vec2 u_WindowSize;

vec2 unpackPair(float packedValue)
{
    float packed = floor(packedValue + 0.5);
    return vec2(
        mod(packed, 256.0) / 255.0,
        mod(floor(packed / 256.0), 256.0) / 255.0);
}

vec3 unpackVideoControls(float packedValue)
{
    float packed = floor(packedValue + 0.5);
    float sceneByte = mod(floor(packed / 65536.0), 256.0);
    if (sceneByte < 0.5)
        return vec3(1.0, 1.0, 1.0);

    return vec3(
        mod(packed, 256.0) / 255.0 * 2.0,
        mod(floor(packed / 256.0), 256.0) / 255.0,
        (sceneByte - 1.0) / 254.0);
}

vec3 sceneLight(vec2 uv, vec2 centre, float packedValue)
{
    vec2 controls = unpackPair(packedValue);
    vec2 delta = (uv - centre) / vec2(0.18, 0.52);
    float glow = exp(-dot(delta, delta) * 2.0);
    vec3 colour = mix(
        vec3(1.0, 0.96, 1.0),
        vec3(0.62, 0.12, 1.0),
        controls.y);
    return colour * controls.x * glow * 0.20;
}

void main(void)
{
    vec2 uv = gl_TexCoord[0].xy;
    vec4 scene = texture2D(texture0, uv) * gl_Color;

    scene.rgb += sceneLight(uv, vec2(0.25, 0.56),
        u_QuickPlayerData.x);
    scene.rgb += sceneLight(uv, vec2(0.50, 0.56),
        u_QuickPlayerData.y);
    scene.rgb += sceneLight(uv, vec2(0.75, 0.56),
        u_QuickPlayerData.z);
    vec3 videoControls = unpackVideoControls(u_QuickPlayerData.w);
    scene.rgb *= videoControls.z;

    if (u_QuickPlayerTextureSize_video1.x <= 0.0 ||
        u_QuickPlayerTextureSize_video1.y <= 0.0)
    {
        gl_FragColor = scene;
        return;
    }

    float videoAspect = u_QuickPlayerTextureSize_video1.x /
        u_QuickPlayerTextureSize_video1.y;
    float windowAspect = u_WindowSize.x / u_WindowSize.y;
    vec2 panelSize = vec2(0.42 * videoAspect / windowAspect, 0.42);
    panelSize *= min(1.0, 0.80 / panelSize.x);
    vec2 panelCentre = vec2(0.5);
    vec2 videoUv = (uv - (panelCentre - panelSize * 0.5)) /
        panelSize;
    if (videoUv.x < 0.0 || videoUv.x > 1.0 ||
        videoUv.y < 0.0 || videoUv.y > 1.0)
    {
        gl_FragColor = scene;
        return;
    }

    videoUv.y = 1.0 - videoUv.y;
    vec4 video = texture2D(u_QuickPlayerTexture_video1, videoUv);
    float luminance = dot(video.rgb, vec3(0.299, 0.587, 0.114));
    video.rgb = mix(video.rgb, vec3(luminance), videoControls.y);
    video.rgb *= videoControls.x;
    gl_FragColor = mix(scene, video, video.a);
}
