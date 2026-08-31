#pragma once

bool TryEvaluateOpenGlHdr(HWND window, int width, int height);
bool TryEvaluateOpenGlTextureHdr(HWND window, unsigned int sourceTexture,
    int textureWidth, int textureHeight, int targetWidth, int targetHeight);
void SetOpenGlHdrClickThrough(HWND sourceWindow, bool enabled);
void SetOpenGlHdrPlayerLocked(bool locked);
void SetOpenGlHdrInteractiveMove(bool active);
void HideOpenGlHdr();
bool HandleOpenGlHdrMouseWheel(WPARAM wParam);
void ReleaseOpenGlHdr();
void SuspendOpenGlHdrSurface();
void ResumeOpenGlHdr();
long OpenGlHdrStatus();
long OpenGlHdrFrameCount();
