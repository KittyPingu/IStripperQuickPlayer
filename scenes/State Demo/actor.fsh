#version 120
uniform sampler2D texture0;

void main()
{
    // Preserve the native clip's transparency and tint while its plane rotates.
    gl_FragColor = texture2D(texture0, gl_TexCoord[0].xy) * gl_Color;
}
