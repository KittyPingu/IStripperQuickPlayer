uniform vec2 u_WindowSize;
uniform float u_QuickPlayerClipBoundsEnabled0;
uniform vec4 u_QuickPlayerClipBounds0;
uniform vec2 u_QuickPlayerClipTargetSize0;
uniform float u_QuickPlayerClipBoundsEnabled1;
uniform vec4 u_QuickPlayerClipBounds1;
uniform vec2 u_QuickPlayerClipTargetSize1;
uniform float u_QuickPlayerClipBoundsEnabled2;
uniform vec4 u_QuickPlayerClipBounds2;
uniform vec2 u_QuickPlayerClipTargetSize2;

float border(vec2 point, vec4 bounds, vec2 targetSize)
{
    vec2 minimum = bounds.xy;
    vec2 maximum = bounds.zw;
    vec2 width = 2.0 / max(targetSize, vec2(1.0));
    bool insideX = point.x >= minimum.x - width.x &&
        point.x <= maximum.x + width.x;
    bool insideY = point.y >= minimum.y - width.y &&
        point.y <= maximum.y + width.y;
    bool vertical = insideY &&
        (abs(point.x - minimum.x) <= width.x ||
         abs(point.x - maximum.x) <= width.x);
    bool horizontal = insideX &&
        (abs(point.y - minimum.y) <= width.y ||
         abs(point.y - maximum.y) <= width.y);
    return vertical || horizontal ? 1.0 : 0.0;
}

void main(void)
{
    vec2 point = gl_FragCoord.xy / u_WindowSize;
    vec3 color = vec3(0.0);
    float visible = 0.0;

    if (u_QuickPlayerClipBoundsEnabled1 > 0.5)
    {
        float line = border(point, u_QuickPlayerClipBounds1,
            u_QuickPlayerClipTargetSize1);
        color += line * vec3(1.0, 0.15, 0.8);
        visible = max(visible, line);
    }
    if (u_QuickPlayerClipBoundsEnabled0 > 0.5)
    {
        float line = border(point, u_QuickPlayerClipBounds0,
            u_QuickPlayerClipTargetSize0);
        color += line * vec3(0.1, 1.0, 1.0);
        visible = max(visible, line);
    }
    if (u_QuickPlayerClipBoundsEnabled2 > 0.5)
    {
        float line = border(point, u_QuickPlayerClipBounds2,
            u_QuickPlayerClipTargetSize2);
        color += line * vec3(1.0, 0.85, 0.1);
        visible = max(visible, line);
    }

    if (visible < 0.5)
        discard;
    gl_FragColor = vec4(min(color, vec3(1.0)), 1.0);
}
