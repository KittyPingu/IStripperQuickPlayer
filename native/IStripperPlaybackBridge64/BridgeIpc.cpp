#include "BridgeIpc.h"
#include <MinHook.h>
#include <cstdint>
#include <string>
#include <vector>

namespace
{
    constexpr std::uint32_t CommandMagic = 0x5142434D;
    constexpr std::uint32_t EventMagic = 0x51424556;
    constexpr std::uint32_t ProtocolVersion = 1;
    constexpr DWORD EventTimeoutMilliseconds = 2'000;
    constexpr HRESULT BridgeSuccess = 1;
    constexpr std::uint32_t MaximumApiNameBytes = 512;
    constexpr std::uint32_t MaximumEventDataBytes = 1024 * 1024;

    using RegSetValueExWAction = LSTATUS(WINAPI*)(HKEY, LPCWSTR, DWORD,
        DWORD, const BYTE*, DWORD);

    HMODULE g_module = nullptr;
    HANDLE g_eventPipe = INVALID_HANDLE_VALUE;
    SRWLOCK g_eventPipeLock = SRWLOCK_INIT;
    RegSetValueExWAction g_originalRegSetValueExW = nullptr;
    void* g_regSetValueExWTarget = nullptr;
    INIT_ONCE g_registryHookOnce = INIT_ONCE_STATIC_INIT;
    HRESULT g_registryHookResult = E_PENDING;
    thread_local bool g_insideRegistryHook = false;

    std::wstring PipeName(const wchar_t* kind)
    {
        return L"\\\\.\\pipe\\IStripperQuickPlayer." +
            std::wstring(kind) + L"." + std::to_wstring(GetCurrentProcessId());
    }

    bool ReadExact(HANDLE pipe, void* buffer, DWORD size)
    {
        auto* destination = static_cast<BYTE*>(buffer);
        while (size > 0)
        {
            DWORD read = 0;
            if (!ReadFile(pipe, destination, size, &read, nullptr) || read == 0)
                return false;
            destination += read;
            size -= read;
        }
        return true;
    }

    bool WriteExact(HANDLE pipe, const void* buffer, DWORD size)
    {
        auto* source = static_cast<const BYTE*>(buffer);
        while (size > 0)
        {
            DWORD written = 0;
            if (!WriteFile(pipe, source, size, &written, nullptr) ||
                written == 0)
                return false;
            source += written;
            size -= written;
        }
        return true;
    }

    bool TimedTransfer(HANDLE pipe, bool write, void* buffer, DWORD size)
    {
        auto* bytes = static_cast<BYTE*>(buffer);
        while (size > 0)
        {
            OVERLAPPED operation = {};
            operation.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!operation.hEvent)
                return false;

            DWORD transferred = 0;
            const BOOL started = write
                ? WriteFile(pipe, bytes, size, nullptr, &operation)
                : ReadFile(pipe, bytes, size, nullptr, &operation);
            DWORD error = started ? ERROR_SUCCESS : GetLastError();
            bool success = false;
            if (started || error == ERROR_IO_PENDING)
            {
                DWORD wait = started ? WAIT_OBJECT_0 :
                    WaitForSingleObject(operation.hEvent,
                        EventTimeoutMilliseconds);
                if (wait == WAIT_OBJECT_0)
                    success = GetOverlappedResult(
                        pipe, &operation, &transferred, FALSE) != FALSE;
                else
                {
                    CancelIoEx(pipe, &operation);
                    WaitForSingleObject(operation.hEvent, INFINITE);
                }
            }
            CloseHandle(operation.hEvent);
            if (!success || transferred == 0)
                return false;
            bytes += transferred;
            size -= transferred;
        }
        return true;
    }

    void CloseEventPipe()
    {
        if (g_eventPipe != INVALID_HANDLE_VALUE)
        {
            CloseHandle(g_eventPipe);
            g_eventPipe = INVALID_HANDLE_VALUE;
        }
    }

    bool SendRegistryEvent(LPCWSTR name, const BYTE* data, DWORD dataSize)
    {
        const DWORD nameSize = static_cast<DWORD>(
            wcslen(name) * sizeof(wchar_t));
        const std::uint32_t header[] = {
            EventMagic, ProtocolVersion, nameSize, dataSize
        };
        int skip = 0;

        AcquireSRWLockExclusive(&g_eventPipeLock);
        bool success = g_eventPipe != INVALID_HANDLE_VALUE &&
            TimedTransfer(g_eventPipe, true,
                const_cast<std::uint32_t*>(header), sizeof(header)) &&
            TimedTransfer(g_eventPipe, true,
                const_cast<wchar_t*>(name), nameSize) &&
            (dataSize == 0 || TimedTransfer(g_eventPipe, true,
                const_cast<BYTE*>(data), dataSize)) &&
            TimedTransfer(g_eventPipe, false, &skip, sizeof(skip));
        if (!success)
            CloseEventPipe();
        ReleaseSRWLockExclusive(&g_eventPipeLock);
        return success && skip != 0;
    }

    LSTATUS WINAPI HookedRegSetValueExW(HKEY key, LPCWSTR valueName,
        DWORD reserved, DWORD type, const BYTE* data, DWORD dataSize)
    {
        if (!g_originalRegSetValueExW)
            return ERROR_INVALID_FUNCTION;

        const bool relevant = valueName && data &&
            dataSize <= MaximumEventDataBytes &&
            (_wcsicmp(valueName, L"CurrentAnim") == 0 ||
             _wcsicmp(valueName, L"PreviousUserLevel") == 0 ||
             _wcsicmp(valueName, L"playingMode") == 0);
        if (relevant && !g_insideRegistryHook)
        {
            g_insideRegistryHook = true;
            const bool skip = SendRegistryEvent(valueName, data, dataSize);
            g_insideRegistryHook = false;
            if (skip)
                return ERROR_SUCCESS;
        }
        return g_originalRegSetValueExW(
            key, valueName, reserved, type, data, dataSize);
    }

    BOOL CALLBACK InstallRegistryHook(PINIT_ONCE, PVOID, PVOID*)
    {
        MH_STATUS status = MH_Initialize();
        if (status != MH_OK && status != MH_ERROR_ALREADY_INITIALIZED)
        {
            g_registryHookResult = E_FAIL;
            return TRUE;
        }

        status = MH_CreateHookApiEx(L"KernelBase.dll", "RegSetValueExW",
            &HookedRegSetValueExW,
            reinterpret_cast<void**>(&g_originalRegSetValueExW),
            &g_regSetValueExWTarget);
        if (status != MH_OK && status != MH_ERROR_ALREADY_CREATED)
        {
            g_registryHookResult = E_FAIL;
            return TRUE;
        }

        status = MH_EnableHook(g_regSetValueExWTarget);
        g_registryHookResult =
            status == MH_OK || status == MH_ERROR_ENABLED
                ? BridgeSuccess : E_FAIL;
        return TRUE;
    }

    HRESULT ConnectEventPipe()
    {
        const std::wstring name = PipeName(L"Events");
        if (!WaitNamedPipeW(name.c_str(), EventTimeoutMilliseconds))
            return HRESULT_FROM_WIN32(GetLastError());

        HANDLE pipe = CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE,
            0, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
        if (pipe == INVALID_HANDLE_VALUE)
            return HRESULT_FROM_WIN32(GetLastError());

        AcquireSRWLockExclusive(&g_eventPipeLock);
        CloseEventPipe();
        g_eventPipe = pipe;
        ReleaseSRWLockExclusive(&g_eventPipeLock);
        return BridgeSuccess;
    }

    struct CommandHeader
    {
        std::uint32_t magic;
        std::uint32_t version;
        std::uint32_t nameSize;
        std::uint32_t hasParameter;
        std::uint64_t parameter;
    };

    void ServeCommands(HANDLE pipe)
    {
        for (;;)
        {
            CommandHeader header = {};
            if (!ReadExact(pipe, &header, sizeof(header)))
                return;
            if (header.magic != CommandMagic ||
                header.version != ProtocolVersion ||
                header.nameSize == 0 ||
                header.nameSize > MaximumApiNameBytes)
                return;

            std::vector<char> name(header.nameSize + 1, '\0');
            if (!ReadExact(pipe, name.data(), header.nameSize))
                return;

            auto function = reinterpret_cast<FARPROC>(
                GetProcAddress(g_module, name.data()));
            HRESULT result = HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);
            if (function)
            {
                result = header.hasParameter
                    ? reinterpret_cast<HRESULT(WINAPI*)(SIZE_T)>(function)(
                        static_cast<SIZE_T>(header.parameter))
                    : reinterpret_cast<HRESULT(WINAPI*)()>(function)();
            }
            if (!WriteExact(pipe, &result, sizeof(result)))
                return;
        }
    }
}

bool SendBridgeEvent(const wchar_t* name, const void* data, DWORD dataSize)
{
    return name != nullptr && dataSize <= MaximumEventDataBytes &&
        SendRegistryEvent(name, static_cast<const BYTE*>(data), dataSize);
}

extern "C" __declspec(dllexport) HRESULT WINAPI
IStripperStartRegistryHook()
{
    HRESULT result = ConnectEventPipe();
    if (result < 0)
        return result;
    InitOnceExecuteOnce(
        &g_registryHookOnce, &InstallRegistryHook, nullptr, nullptr);
    return g_registryHookResult;
}

DWORD WINAPI StartBridgeCommandServer(void* module)
{
    g_module = static_cast<HMODULE>(module);
    const std::wstring name = PipeName(L"Commands");
    for (;;)
    {
        HANDLE pipe = CreateNamedPipeW(name.c_str(),
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, 4096, 4096, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE)
            return GetLastError();

        const BOOL connected = ConnectNamedPipe(pipe, nullptr)
            ? TRUE : GetLastError() == ERROR_PIPE_CONNECTED;
        if (connected)
            ServeCommands(pipe);
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
}
