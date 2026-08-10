uniform sampler2D texture0;

uniform sampler2D u_QuickPlayerTexture_video1;
uniform vec2 u_QuickPlayerTextureSize_video1;
uniform float u_QuickPlayerTextureSequence_video1;
uniform vec4 u_QuickPlayerData;

vec2 unpackLight(float packedValue)
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

vec3 stageLight(vec2 pixel, vec2 centre, float packedValue)
{
    vec2 controls = unpackLight(packedValue);
    float brightness = controls.x;
    float purpleness = controls.y;
    vec3 lightColour = mix(
        vec3(1.0, 0.96, 1.0),
        vec3(0.62, 0.12, 1.0),
        purpleness);

    vec2 bodyDelta = (pixel - centre) / vec2(225.0, 430.0);
    float bodyGlow = exp(-dot(bodyDelta, bodyDelta) * 2.2);

    vec2 floorDelta = (pixel - vec2(centre.x, 910.0)) /
        vec2(205.0, 72.0);
    float floorGlow = exp(-dot(floorDelta, floorDelta) * 2.0);

    return lightColour * brightness *
        (bodyGlow * 0.13 + floorGlow * 0.22);
}

void main(void)
{
    vec2 sceneUv = gl_TexCoord[0].xy;
    vec2 pixel = sceneUv * vec2(1920.0, 1080.0);
    vec4 scene = texture2D(texture0, sceneUv) * gl_Color;

    scene.rgb += stageLight(
        pixel, vec2(582.0, 650.0), u_QuickPlayerData.x);
    scene.rgb += stageLight(
        pixel, vec2(976.0, 650.0), u_QuickPlayerData.y);
    scene.rgb += stageLight(
        pixel, vec2(1402.0, 650.0), u_QuickPlayerData.z);
    vec3 videoControls = unpackVideoControls(u_QuickPlayerData.w);
    scene.rgb *= videoControls.z;

    if (u_QuickPlayerTextureSize_video1.x <= 0.0 ||
        u_QuickPlayerTextureSize_video1.y <= 0.0)
    {
        gl_FragColor = scene;
        return;
    }

    // Inverse projective transform for the empty left frame.
    // Frame corners in the 1920 x 1080 scene:
    //   top-left       91.866, 138.874
    //   top-right     334.163, 259.384
    //   bottom-right  329.569, 697.811
    //   bottom-left    84.976, 733.390
    if (pixel.x < 82.0 || pixel.x > 338.0 ||
        pixel.y < 136.0 || pixel.y > 736.0)
    {
        gl_FragColor = scene;
        return;
    }

    float divisor = -0.0010791399 * pixel.x -
        0.0000079391 * pixel.y + 1.1002388;
    vec2 frameUv = vec2(
        (0.0030266379 * pixel.x + 0.0000350762 * pixel.y -
            0.2829164) / divisor,
        (-0.0008340530 * pixel.x + 0.0016769405 * pixel.y -
            0.1562615) / divisor);

    float edgeDistance = min(
        min(frameUv.x, 1.0 - frameUv.x),
        min(frameUv.y, 1.0 - frameUv.y));
    float edgeWidth = max(fwidth(frameUv.x), fwidth(frameUv.y));
    if (edgeDistance <= -edgeWidth)
    {
        gl_FragColor = scene;
        return;
    }

    // Centre-cropped cover fit: the centres of the frame and video align.
    float frameAspect = 0.55;
    float videoAspect = u_QuickPlayerTextureSize_video1.x /
        u_QuickPlayerTextureSize_video1.y;
    vec2 videoUv = clamp(frameUv, vec2(0.0), vec2(1.0));
    if (videoAspect > frameAspect)
    {
        float visibleWidth = frameAspect / videoAspect;
        videoUv.x = 0.5 + (videoUv.x - 0.5) * visibleWidth;
    }
    else
    {
        float visibleHeight = videoAspect / frameAspect;
        videoUv.y = 0.5 + (videoUv.y - 0.5) * visibleHeight;
    }

    videoUv.y = 1.0 - videoUv.y;
    vec4 video = texture2D(u_QuickPlayerTexture_video1, videoUv);
    float luminance = dot(video.rgb, vec3(0.299, 0.587, 0.114));
    video.rgb = mix(video.rgb, vec3(luminance), videoControls.y);
    video.rgb *= videoControls.x;

    float coverage = smoothstep(-edgeWidth, edgeWidth, edgeDistance);
    float alpha = video.a * coverage;
    gl_FragColor = vec4(mix(scene.rgb, video.rgb, alpha),
        max(scene.a, alpha));
}
