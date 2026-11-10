#version 120
uniform sampler2D texture0; // PreviousState: four RGB records.
uniform sampler2D texture1; // ActorMask: alpha copied into RGB.
const vec2 SIZE = vec2(960.0, 540.0);
const float RADIUS = 18.0;
const float MAX_SPEED = 8.0;

vec3 record(float slot)
{
    return floor(texture2D(texture0, vec2((slot + 0.5) / 4.0, 0.5)).rgb * 255.0 + 0.5);
}
float unpack16(vec3 bytes) { return (bytes.r * 256.0 + bytes.g) / 65535.0; }
vec4 pack16(float value)
{
    float n = floor(clamp(value, 0.0, 1.0) * 65535.0 + 0.5);
    return vec4(floor(n / 256.0) / 255.0, mod(n, 256.0) / 255.0, 73.0 / 255.0, 1.0);
}
float maskAt(vec2 pixel)
{
    if (pixel.x < 0.0 || pixel.y < 0.0 || pixel.x >= SIZE.x || pixel.y >= SIZE.y) return 0.0;
    return texture2D(texture1, pixel / SIZE).r;
}
// Sobel gradient in scene pixels. Increasing alpha points INTO the actor.
vec2 maskGradient(vec2 p)
{
    float a = maskAt(p + vec2(-1, -1)), b = maskAt(p + vec2(0, -1));
    float c = maskAt(p + vec2(1, -1)), d = maskAt(p + vec2(-1, 0));
    float e = maskAt(p + vec2(1, 0)), f = maskAt(p + vec2(-1, 1));
    float g = maskAt(p + vec2(0, 1)), h = maskAt(p + vec2(1, 1));
    return vec2(c + 2.0*e + h - a - 2.0*d - f,
                f + 2.0*g + h - a - 2.0*b - c);
}
void main()
{
    vec3 rx = record(0.0), ry = record(1.0), rvx = record(2.0), rvy = record(3.0);
    vec2 position = vec2(100.0, 270.0);
    vec2 velocity = vec2(3.0, 1.5);
    bool valid = rx.b == 73.0 && ry.b == 73.0 && rvx.b == 73.0 && rvy.b == 73.0;
    if (valid) {
        position = vec2(unpack16(rx), unpack16(ry)) * SIZE;
        velocity = (vec2(unpack16(rvx), unpack16(rvy)) * 2.0 - 1.0) * MAX_SPEED;
        valid = all(greaterThanEqual(position, vec2(RADIUS - 0.02))) &&
                all(lessThanEqual(position, SIZE - RADIUS + 0.02)) && length(velocity) <= MAX_SPEED + 0.001;
    }
    if (!valid) {
        position = vec2(100.0, 270.0);
        velocity = vec2(3.0, 1.5);
    }
    else {
        position += velocity;
        if (position.x < RADIUS || position.x > SIZE.x - RADIUS) velocity.x = -velocity.x;
        if (position.y < RADIUS || position.y > SIZE.y - RADIUS) velocity.y = -velocity.y;
        position = clamp(position, vec2(RADIUS), SIZE - RADIUS);

        bool overlaps = false;
        float closest = 1.0e10;
        vec2 contact = position, normal = vec2(0.0);
        // Find the nearest local alpha contour touched by the circular ball.
        for (int y = -19; y <= 19; ++y) {
            for (int x = -19; x <= 19; ++x) {
                vec2 pixel = floor(position) + vec2(0.5) + vec2(float(x), float(y));
                vec2 delta = pixel - position;
                float distanceSquared = dot(delta, delta);
                if (distanceSquared > (RADIUS + 1.0) * (RADIUS + 1.0)) continue;
                if (maskAt(pixel) <= 0.1) continue;
                if (distanceSquared <= RADIUS * RADIUS) overlaps = true;
                vec2 gradient = maskGradient(pixel);
                if (dot(gradient, gradient) > 0.001 && distanceSquared < closest) {
                    closest = distanceSquared;
                    contact = pixel;
                    normal = normalize(-gradient);
                }
            }
        }
        if (overlaps && closest < 1.0e10) {
            // Reflect about the outward normal, i.e. the perpendicular to the tangent.
            if (dot(velocity, normal) < 0.0) velocity = reflect(velocity, normal);
            position = clamp(contact + normal * (RADIUS + 1.5), vec2(RADIUS), SIZE - RADIUS);
        }
    }
    float slot = floor(gl_TexCoord[0].x * 4.0);
    float value = slot < 0.5 ? position.x / SIZE.x :
                  slot < 1.5 ? position.y / SIZE.y :
                  slot < 2.5 ? velocity.x / (2.0 * MAX_SPEED) + 0.5 :
                               velocity.y / (2.0 * MAX_SPEED) + 0.5;
    gl_FragColor = pack16(value);
}
