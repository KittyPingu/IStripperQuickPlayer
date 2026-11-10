#version 120
uniform sampler2D texture0;
void main()
{
    float alpha = texture2D(texture0, gl_TexCoord[0].xy).a * gl_Color.a;
    gl_FragColor = vec4(vec3(alpha), 1.0);
}
