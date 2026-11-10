# Included scenes

`Midnight Gallery Club` is installed into iStripper's local scene directory by
the QuickPlayer installer. It uses the named `video1` shader texture and the
packed scene-lighting values sent by the `ShaderVideoStreamer` example.

Generated iStripper `.cache` files are intentionally not stored here.

`State Demo` documents framebuffer state retention with a bouncing ball and
uses bridge v165's `u_QuickPlayerMouse` to turn the visible actor plane toward
the cursor. Copy its folder into iStripper's scenes directory to try it; it
is not automatically installed by the QuickPlayer installer.

`Ball Tracking` is a standalone native scene with framebuffer position/velocity
records, a live actor alpha mask, and ball reflection about the local contour
normal. It has no mouse input or QuickPlayer bridge dependency. Copy its folder
into iStripper's scenes directory to try it.
