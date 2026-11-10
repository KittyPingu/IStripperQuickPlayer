#version 120
uniform sampler2D texture0;

// RGB8 record: R/G = high/low position bytes; B = direction + validity marker.
// Every texel holds the same record, so filtering and vertical flips are harmless.
const float RIGHT = 73.0;
const float LEFT = 91.0;
const float STEP = 256.0;
const float MIN_X = 4096.0;
const float MAX_X = 61439.0;

void main()
{
    vec3 bytes = floor(texture2D(texture0, vec2(0.5)).rgb * 255.0 + 0.5);
    float x = bytes.r * 256.0 + bytes.g;
    float direction = bytes.b == LEFT ? -1.0 : 1.0;

    // Missing/invalid memory starts a fresh run; GLSL variables alone do not persist.
    if ((bytes.b != RIGHT && bytes.b != LEFT) || x < MIN_X || x > MAX_X) {
        x = 32768.0;
        direction = 1.0;
    }
    else {
        x += direction * STEP;
        if (x >= MAX_X) {
            x = MAX_X;
            direction = -1.0;
        }
        else if (x <= MIN_X) {
            x = MIN_X;
            direction = 1.0;
        }
    }

    float highByte = floor(x / 256.0);
    float lowByte = x - highByte * 256.0;
    float marker = direction > 0.0 ? RIGHT : LEFT;
    // Alpha is output opacity, never a stored variable in this feedback route.
    gl_FragColor = vec4(vec3(highByte, lowByte, marker) / 255.0, 1.0);
}
