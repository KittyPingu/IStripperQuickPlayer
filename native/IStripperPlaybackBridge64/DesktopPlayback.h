#pragma once
#include <Windows.h>
#include <cstdint>

struct DesktopSnapshot
{
    std::uint32_t size = sizeof(DesktopSnapshot);
    std::uint32_t version = 1;
    std::uint64_t sequence = 0;
    std::uint64_t instance = 0;
    std::uint64_t completedInstance = 0;
    std::int32_t state = 0;
    std::int32_t elapsed = 0;
    std::int32_t duration = 0;
    std::int32_t decoder = 0;
    std::int32_t seekReady = 0;
    std::int32_t readinessMask = 0;
    std::uint64_t window = 0;
    RECT bounds = {};
    std::uint32_t dpi = 0;
    std::uint32_t moving = 0;
    wchar_t monitor[32] = {};
    wchar_t path[1024] = {};
    std::uint64_t request = 0;
};

struct DesktopSelection
{
    std::uint32_t size = sizeof(DesktopSelection);
    std::uint32_t version = 1;
    std::uint64_t instance = 0;
    std::uint64_t request = 0;
    wchar_t path[1024] = {};
};
