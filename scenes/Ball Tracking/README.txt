BALL TRACKING - native iStripper scene; no mouse or QuickPlayer dependency.

Copy this folder into iStripper's data/scenes directory and open Ball Tracking.
A playable actor clip is required to keep fullscreen running.

The ball moves in 2D and bounces off the actor's LIVE alpha silhouette, reflecting
about the local outward surface normal (perpendicular to the edge tangent).
A glancing hit therefore changes both velocity components, rather than simply
reversing horizontal direction.

FRAMEBUFFER VARIABLES
Memory -> PreviousState -> ActorMask -> NextState -> visible scene -> feedback.
The 4x1 state framebuffer stores four scalars: X, Y, velocity X, velocity Y.
Each scalar uses R/G as a 16-bit number and B=73 as a validity marker. Alpha is
not persistent data. Position is normalized to 960x540; velocity is mapped
from -8..8 scene pixels per rendered update. Missing state initializes the ball
at (100,270), travelling (3,1.5) pixels per update.

HOW THE STATE SURVIVES BETWEEN FRAMES
GLSL variables such as position and velocity only exist during one shader
invocation. The memory comes from keeping their values in texture pixels,
then reading those pixels again on the following frame.

The state texture is just four pixels wide and one pixel high:
    Pixel 0: ball X position
    Pixel 1: ball Y position
    Pixel 2: horizontal velocity
    Pixel 3: vertical velocity
These are data records, not colours intended to appear on screen.

At the start of each frame, PreviousState copies the retained Memory texture.
update.fsh reads those four records through texture0, reconstructs position
and velocity, advances the ball, and applies any collision response. Each of
the four output fragments writes its own record into NextState. All four read
the same old snapshot, so a record cannot accidentally read a value another
fragment has just updated.

draw.fsh reads the new position from NextState to draw the ball. The final,
invisible sprite also references NextState: together with Memory's Empty/
source, this is the native feedback pattern that makes those pixels available
to the next frame. Keep that sprite last. No file is saved between frames.

Each value is converted to 0..1 before storage. X is divided by 960, Y by 540,
and velocity v is stored as v/16 + 0.5 so negative values fit too. pack16()
rounds this normalized value to an integer from 0 to 65535, puts the high byte
in red and the low byte in green. Blue holds the validity marker 73 (73/255
in GLSL colour units). Alpha is not used as memory.

To read a record, unpack16() reverses that packing:
    normalized value = (redByte * 256 + greenByte) / 65535
Multiply by 960 or 540 to recover a position; use (value * 2 - 1) * 8 for a
velocity. Sampling each pixel at its centre avoids mixing adjacent records.
The four sample X coordinates are 0.125, 0.375, 0.625 and 0.875, with Y=0.5.

If the markers or decoded values are invalid, the updater initializes position
to (100,270) and velocity to (3,1.5). On the next update it reads that stored
state and moves towards (103,271.5), subject to collisions and small packing
rounding errors. A bounce stores the reflected velocity, so subsequent frames
continue in the new direction. State lasts while the feedback textures survive;
it is not a saved setting and should not be relied on across scene reloads.

COLLISION
mask.fsh writes the actor's alpha into RGB in a 960x540 framebuffer. Mask and
visible actor use identical placement, so the mask matches the displayed clip.
update.fsh tests the circular ball against alpha > 0.1. It finds the nearest
local contour pixel and estimates its outward normal with a 3x3 Sobel gradient.
For incoming velocity v and unit normal n, reflection is v - 2*dot(v,n)*n.
Only incoming motion reflects; separating motion is left alone. The ball is
moved outside the contour to reduce repeated penetration. The updated position
and velocity are written back into the four RGB records for the next frame.

KEEP the invisible NextState sprite as the final rendered element. Rendering
anything afterwards breaks the feedback pattern. Keep the duplicated actor
geometry in the mask and final camera in sync when editing the scene.
