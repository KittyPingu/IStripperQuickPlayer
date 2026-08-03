#pragma once

#include <Windows.h>

DWORD WINAPI StartBridgeCommandServer(void* module);
bool SendBridgeEvent(const wchar_t* name, const void* data, DWORD dataSize);
