#version 120
uniform sampler2D texture0;
uniform float ballOnly;

void main()
{
    vec3 bytes = floor(texture2D(texture0, vec2(0.5)).rgb * 255.0 + 0.5);
    float x = (bytes.r * 256.0 + bytes.g) / 65535.0;
    bool valid = bytes.b == 73.0 || bytes.b == 91.0;

    vec2 uv = gl_TexCoord[0].xy;
    vec2 offset = (uv - vec2(x, 0.5)) * vec2(960.0, 540.0);
    float ball = 1.0 - smoothstep(17.0, 19.0, length(offset));
    float rail = 1.0 - smoothstep(1.0, 2.0, abs(offset.y));
    vec3 background = mix(vec3(0.025, 0.035, 0.055), vec3(0.15), rail);
    // Magenta means the draw pass did not receive a valid state record.
    vec3 colour = valid ? vec3(0.15, 0.85, 1.0) : vec3(1.0, 0.0, 1.0);
    gl_FragColor = ballOnly > 0.5
        ? vec4(colour, ball)
        : vec4(mix(background, colour, ball), 1.0);
}
