#version 120
uniform sampler2D texture0;
uniform vec2 u_WindowSize;
uniform float ballOnly;
float readValue(float slot)
{
    vec2 bytes = floor(texture2D(texture0, vec2((slot + 0.5) / 4.0, 0.5)).rg * 255.0 + 0.5);
    return (bytes.r * 256.0 + bytes.g) / 65535.0;
}
void main()
{
    vec2 pixel = gl_FragCoord.xy / u_WindowSize * vec2(960.0, 540.0);
    vec2 centre = vec2(readValue(0.0), readValue(1.0)) * vec2(960.0, 540.0);
    float ball = 1.0 - smoothstep(17.0, 18.0, length(pixel - centre));
    vec3 colour = vec3(0.15, 0.85, 1.0);
    gl_FragColor = ballOnly > 0.5 ? vec4(colour, ball) :
        vec4(mix(vec3(0.025, 0.035, 0.055), colour, ball), 1.0);
}
