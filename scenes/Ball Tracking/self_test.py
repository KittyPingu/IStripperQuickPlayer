"""Small CPU checks; does not exercise iStripper framebuffer feedback."""
from pathlib import Path
import math
import re

root = Path(__file__).parent

def reflect(v, n):
    incoming = sum(a*b for a, b in zip(v, n))
    return tuple(a - 2*incoming*b for a, b in zip(v, n)) if incoming < 0 else v

def sobel(alpha):
    a,b,c,d,e,f,g,h = [alpha(x,y) for x,y in
        [(-1,-1),(0,-1),(1,-1),(-1,0),(1,0),(-1,1),(0,1),(1,1)]]
    gradient = (c+2*e+h-a-2*d-f, f+2*g+h-a-2*b-c)
    length = math.hypot(*gradient)
    return tuple(-v/length for v in gradient)

for x in range(65536):
    r,g = x//256, x%256
    assert int(r/255*255+0.5)*256 + int(g/255*255+0.5) == x
for alpha, expected in [(lambda x,y: float(x>=0), (-3,1.5)),
                        (lambda x,y: float(y>=0), (3,-1.5)),
                        (lambda x,y: float(x+y>=0), (-1.5,-3))]:
    normal = sobel(alpha)
    v = (3,1.5)
    bounced = reflect(v, normal)
    assert all(abs(a-b)<1e-9 for a,b in zip(bounced, expected))
    assert abs(math.hypot(*v)-math.hypot(*bounced))<1e-9
    tangent = (-normal[1],normal[0])
    assert abs(sum(a*b for a,b in zip(v,tangent))-
               sum(a*b for a,b in zip(bounced,tangent)))<1e-9
assert reflect((-3,1.5),(-1,0)) == (-3,1.5)
scene = (root/'Ball Tracking.scn').read_text()
assert scene.index('id: PreviousState') < scene.index('id: ActorMask') < scene.index('id: NextState')
assert 'source: PreviousState, 1' not in scene
assert 'shader: vertex' not in scene
assert scene.rstrip().endswith('opacity: 0.0\n        source: NextState\n    }\n}')
for name in re.findall(r'shader: (?:fragment|vertex), (\S+)',scene):
    assert (root/name).is_file()
for path in root.iterdir():
    if path.suffix in ('.scn','.fsh','.vsh'):
        assert 'u_QuickPlayer' not in path.read_text()
print('PASS: RGB packing, contour normals, tangential velocity, speed and scene sources')
