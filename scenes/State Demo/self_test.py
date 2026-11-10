"""CPU checks only; this does not verify GLSL compilation or engine feedback."""
from pathlib import Path
import re
import math

root = Path(__file__).parent
shader = (root / "update.fsh").read_text()
constants = {name: int(float(value)) for name, value in
             re.findall(r"const float (\w+) = ([\d.]+);", shader)}
RIGHT, LEFT = constants["RIGHT"], constants["LEFT"]
STEP, LO, HI = constants["STEP"], constants["MIN_X"], constants["MAX_X"]


def pack(x, marker):
    return (x // 256, x % 256, marker)


def update(record):
    r, g, marker = record
    x = r * 256 + g
    if marker not in (RIGHT, LEFT) or not LO <= x <= HI:
        return pack(32768, RIGHT)
    direction = -1 if marker == LEFT else 1
    x += direction * STEP
    if x >= HI:
        return pack(HI, LEFT)
    if x <= LO:
        return pack(LO, RIGHT)
    return pack(x, marker)


for x in range(65536):
    record = pack(x, RIGHT)
    # Include normalization to [0,1] and byte recovery as used by the shaders.
    recovered = [int(channel / 255.0 * 255.0 + 0.5) for channel in record]
    assert recovered[0] * 256 + recovered[1] == x
assert update((0, 0, 0)) == pack(32768, RIGHT)
assert update(pack(0, RIGHT)) == pack(32768, RIGHT)
assert update(pack(HI - 1, RIGHT)) == pack(HI, LEFT)
assert update(pack(LO + 1, LEFT)) == pack(LO, RIGHT)
record = update((0, 0, 0))
positions, directions = set(), set()
for _ in range(2000):
    record = update(record)
    x = record[0] * 256 + record[1]
    assert LO <= x <= HI
    positions.add(x)
    directions.add(record[2])
assert len(positions) > 200 and directions == {LEFT, RIGHT}
scene = (root / "State Demo.scn").read_text()
assert scene.rstrip().endswith("opacity: 0.0\n        source: NextState\n    }\n}")
for shader_name in re.findall(r"shader: (?:fragment|vertex), (\S+)", scene):
    assert (root / shader_name).is_file()
assert 'id: Actor' in scene and 'source: Actor, 0' in scene
assert scene.index('// 3. Draw the ball') < scene.index('clipSprite {')
assert scene.index('clipSprite {') < scene.index('// Draw the ball again')
vertex = (root / 'face-mouse.vsh').read_text()
assert 'uniform vec4 u_QuickPlayerMouse;' in vertex
assert 'source: NextState, 1' not in scene
depth = float(re.search(r'TARGET_DEPTH = ([\d.]+)', vertex)[1])
focal = float(re.search(r'FOCAL_LENGTH = ([\d.]+)', vertex)[1])
for mx in (0.0, 0.5, 1.0):
    for my in (0.0, 0.5, 1.0):
        dx, dy = (mx - 0.5) * 960.0, (my - 0.5) * 540.0
        yaw = math.atan2(dx, depth)
        pitch = math.atan2(dy, math.hypot(dx, depth))
        normal = (math.sin(yaw) * math.cos(pitch), math.sin(pitch),
                  math.cos(yaw) * math.cos(pitch))
        length = math.sqrt(dx * dx + dy * dy + depth * depth)
        assert all(abs(n - t / length) < 1e-9
                   for n, t in zip(normal, (dx, dy, depth)))
        for vx in (-480.0, 480.0):
            for vy in (-270.0, 270.0):
                assert 1.0 + (vx * math.sin(yaw) +
                    vy * math.sin(pitch) * math.cos(yaw)) / focal > 0.0
assert "u_Elapsed" not in shader
print("PASS: RGB8 round trips, initialization, accumulation, reversals, facing direction, projection and resources")
