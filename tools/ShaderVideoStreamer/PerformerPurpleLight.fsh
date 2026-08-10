uniform sampler2D texture0;
uniform vec4 u_QuickPlayerData;
uniform float lightIndex;

vec2 unpackLight(float packedValue)
{
    float packed = floor(packedValue + 0.5);
    return vec2(
        mod(packed, 256.0) / 255.0,
        mod(floor(packed / 256.0), 256.0) / 255.0);
}

float unpackSceneBrightness(float packedValue)
{
    float packed = floor(packedValue + 0.5);
    float encoded = mod(floor(packed / 65536.0), 256.0);
    if (encoded < 0.5)
        return 1.0;
    return (encoded - 1.0) / 254.0;
}

void main(void)
{
    vec4 performer = texture2D(texture0, gl_TexCoord[0].xy) * gl_Color;

    float packed = u_QuickPlayerData.x;
    if (lightIndex > 0.5)
        packed = u_QuickPlayerData.y;
    if (lightIndex > 1.5)
        packed = u_QuickPlayerData.z;

    vec2 controls = unpackLight(packed);
    float brightness = controls.x;
    float purpleness = controls.y;
    vec3 purpleLight = vec3(0.85, 0.0, 1.85);

    // Keep a very small amount of ambient light at zero, then use a curved
    // response so the useful middle of the slider does not feel too dark.
    float illumination = mix(0.08, 1.0, sqrt(brightness));
    performer.rgb *= illumination * (1.0 + brightness * 0.32);
    float tintStrength = purpleness *
        mix(0.78, 0.98, sqrt(brightness));
    performer.rgb = mix(
        performer.rgb,
        performer.rgb * purpleLight,
        tintStrength);
    performer.rgb += purpleLight * performer.a *
        brightness * purpleness * 0.075;
    performer.rgb *= unpackSceneBrightness(u_QuickPlayerData.w);

    gl_FragColor = performer;
}
