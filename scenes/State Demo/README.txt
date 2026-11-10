STATE DEMO - native iStripper fullscreen framebuffer persistence

INSTALL
Copy this entire folder into your iStripper data/scenes directory (one folder
below scenes), then open State Demo in iStripper's fullscreen scene picker.
Keep all SCN/shader files together. iStripper needs an available playable clip
to keep fullscreen running; Actor supplies this and is now visible. No bundled
images, QuickPlayer integration or external shader tools are required.

WHAT TO EXPECT
A cyan ball travels along a horizontal rail and reverses at either end.
The visible actor plane turns/tilts toward the mouse. The ball still moves
independently to demonstrate state retention.
Its position is updated from the previous frame's stored position. Neither
shader uses elapsed time, sin(), animate, or a global variable as memory.
If the ball stays at the centre, feedback may not be reaching the next frame.
Magenta means the drawing shader received an invalid state record.

ACTOR FACING THE MOUSE (requires QuickPlayer bridge v165)
Keep QuickPlayer running with playback control enabled. Restart iStripper after
upgrading the bridge, then reopen this scene. The bridge publishes:
    uniform vec4 u_QuickPlayerMouse;
XY is normalized relative to the OpenGL window (bottom-left 0,0; top-right 1,1).
Z is 1 when inside the window; W is 1 when coordinates are valid. These are not
mouse-button states. No REST calls or SCN uniform values are needed.

face-mouse.vsh clamps XY to the window and rotates the actor plane's normal
toward (mouseX - centreX, mouseY - centreY, TARGET_DEPTH) each frame. If the
bridge is absent, W defaults to zero and the plane faces straight forward.
This rotates/tilts a flat recording, not the performer's pose or eye gaze.

The camera is 960x540, with a centred actor pivot. TARGET_DEPTH = 360 defines
virtual depth towards the viewer; larger values reduce the amount of turning.
FOCAL_LENGTH = 720 controls perspective. Camera/pivot changes must be matched
in the vertex shader. The plane follows both horizontal and vertical movement.
The ball is drawn in front of the actor, and the feedback sprite stays LAST.

FULLSCREEN LIFETIME
The scene must retain its Actor clip and clipSprite to keep playback alive.

THE FOUR STAGES (keep this order)
1. Memory / Empty/ supplies the feedback texture, following the existing scene
   pattern. Empty/ is intentional: do not replace it with a PNG or create an
   image directory for it. This is undocumented engine behaviour.
2. PreviousState copies the retained state; NextState runs update.fsh on it.
3. draw.fsh positions the ball; face-mouse.vsh turns the actor from bridge input.
4. The final invisible sprite references NextState. The reference scene warns
   that nothing may be rendered after this sprite or feedback will break.

STATE FORMAT
Each RGB pixel is one record: red = position high byte, green = low byte,
blue = 73 (right) or 91 (left). Position = R * 256 + G, in 0..65535 units.
The blue value doubles as a validity marker for first-frame initialization.
The updater starts at 32768 and adds/subtracts STEP each update; the limits
are MIN_X and MAX_X. Invalid records reset to the initial state.
Alpha is always 1 and is not used for persistent data.

The 64x64 texture repeats ONE record in every pixel. That keeps the example
independent of orientation and texture filtering. For multiple records, assign
specific texels and sample their centres, preserving untouched records.
This is rendered-frame memory, not a save file. Do not rely on it surviving
scene reloads, fullscreen exits, application restarts or context recreation.

EXPERIMENT
Change STEP in update.fsh to alter the distance per update. This intentionally
makes speed depend on rendering rate: the example demonstrates persistence,
not time-based physics. A real simulation should use a bounded time step.
To add actor interaction later, sample the actor mask in the update pass and
change the stored direction/velocity on collision; this demo has no collisions
with an actor and does not provide actor tracking.

CHECKS AND LIMITS
Run `python self_test.py` to check RGB8 packing, initialization, accumulation,
reversal and plane-facing geometry mathematically. It does not run GLSL or test engine feedback.
All four shaders compile in a local Windows OpenGL context, and the actor
vertex/fragment pair links successfully. CPU state/facing checks also pass.
The mouse-facing extension has not yet been verified live in iStripper. Its feedback
layout follows the locally installed Fingals Cave v1.00.04 scene; the engine's
undocumented behaviour may depend on version, pass ordering or scene context.

CREDIT
The installed Fingals Cave scene credits Z22 for original persistent
framebuffer behaviour and Calgon for its database/shader framework.
This example uses newly written shaders and no assets from that scene.
