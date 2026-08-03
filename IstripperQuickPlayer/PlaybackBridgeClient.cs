using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace IStripperQuickPlayer.Interop;

public sealed class PlaybackBridgeClient : IDisposable
{
    private const uint CommandMagic = 0x5142434D;
    private const uint EventMagic = 0x51424556;
    private const uint ProtocolVersion = 1;
    private const int ConnectTimeoutMilliseconds = 5_000;
    private const uint ProcessAccess =
        0x0002 | 0x0400 | 0x0008 | 0x0020 | 0x0010;
    private const uint MemCommitReserve = 0x1000 | 0x2000;
    private const uint PageReadWrite = 0x04;
    private const uint Infinite = 0xFFFFFFFF;

    private readonly int processId;
    private readonly Func<string, byte[], bool> registryWrite;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ManualResetEventSlim eventReady = new();
    private readonly object commandLock = new();
    private readonly Task eventTask;
    private NamedPipeClientStream? commandPipe;
    private BinaryReader? commandReader;
    private BinaryWriter? commandWriter;

    private PlaybackBridgeClient(int processId,
        Func<string, byte[], bool> registryWrite)
    {
        this.processId = processId;
        this.registryWrite = registryWrite;
        eventTask = Task.Run(EventLoop);
        if (!eventReady.Wait(ConnectTimeoutMilliseconds))
            throw new IOException(
                "The iStripper bridge event channel did not start.");
    }

    public bool IsConnected => commandPipe?.IsConnected == true;

    public static PlaybackBridgeClient Attach(int processId,
        string bridgePath, Func<string, byte[], bool> registryWrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bridgePath);
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException(
                "The x64 iStripper bridge requires the x64 QuickPlayer.");

        var client = new PlaybackBridgeClient(processId, registryWrite);
        try
        {
            if (BridgeIsLoaded(processId, bridgePath))
            {
                try
                {
                    client.ConnectCommands(1_000);
                }
                catch
                {
                    throw new IOException(
                        "An older QuickPlayer bridge is already loaded. " +
                        "Restart iStripper once to finish the MinHook upgrade.");
                }
            }
            else
            {
                client.Inject(bridgePath);
                client.ConnectCommands(ConnectTimeoutMilliseconds);
            }
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public int Call(string apiName, ulong? parameter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiName);
        byte[] name = Encoding.UTF8.GetBytes(apiName);
        if (name.Length > 512)
            throw new ArgumentOutOfRangeException(nameof(apiName));

        lock (commandLock)
        {
            if (commandPipe?.IsConnected != true ||
                commandReader == null || commandWriter == null)
                throw new IOException("The iStripper bridge is not connected.");

            try
            {
                WriteCommand(commandWriter, name, parameter);
                commandWriter.Flush();
                return commandReader.ReadInt32();
            }
            catch
            {
                DisconnectCommands();
                throw;
            }
        }
    }

    public int CallUtf8(string apiName, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        byte[] data = Encoding.UTF8.GetBytes(value + "\0");
        if (data.Length > 4 * 1024)
            throw new ArgumentOutOfRangeException(nameof(value));
        nint process = OpenProcess(ProcessAccess, false, processId);
        if (process == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        nint remoteValue = 0;
        try
        {
            remoteValue = VirtualAllocEx(process, 0, (nuint)data.Length,
                MemCommitReserve, PageReadWrite);
            if (remoteValue == 0 ||
                !WriteProcessMemory(process, remoteValue, data,
                    (nuint)data.Length, out nuint written) ||
                written != (nuint)data.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return Call(apiName, (ulong)(nuint)remoteValue);
        }
        finally
        {
            if (remoteValue != 0)
                VirtualFreeEx(process, remoteValue, 0, 0x8000);
            CloseHandle(process);
        }
    }

    public int StartRegistryHook() => Call("IStripperStartRegistryHook");

    internal static bool VerifyProtocol()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            WriteCommand(writer, Encoding.UTF8.GetBytes("Test"), 42);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        return reader.ReadUInt32() == CommandMagic &&
            reader.ReadUInt32() == ProtocolVersion &&
            reader.ReadUInt32() == 4 &&
            reader.ReadUInt32() == 1 &&
            reader.ReadUInt64() == 42 &&
            Encoding.UTF8.GetString(reader.ReadBytes(4)) == "Test";
    }

    private static void WriteCommand(BinaryWriter writer, byte[] name,
        ulong? parameter)
    {
        writer.Write(CommandMagic);
        writer.Write(ProtocolVersion);
        writer.Write((uint)name.Length);
        writer.Write(parameter.HasValue ? 1u : 0u);
        writer.Write(parameter.GetValueOrDefault());
        writer.Write(name);
    }

    private void ConnectCommands(int timeoutMilliseconds)
    {
        var pipe = new NamedPipeClientStream(".", CommandPipeName(processId),
            PipeDirection.InOut, PipeOptions.None);
        try
        {
            pipe.Connect(timeoutMilliseconds);
            commandPipe = pipe;
            commandReader = new BinaryReader(pipe, Encoding.UTF8, true);
            commandWriter = new BinaryWriter(pipe, Encoding.UTF8, true);
        }
        catch
        {
            pipe.Dispose();
            throw new IOException("The MinHook playback bridge did not start.");
        }
    }

    private static bool BridgeIsLoaded(int processId, string bridgePath)
    {
        using Process process = Process.GetProcessById(processId);
        string moduleName = Path.GetFileName(bridgePath);
        return process.Modules.Cast<ProcessModule>().Any(module =>
            string.Equals(module.ModuleName, moduleName,
                StringComparison.OrdinalIgnoreCase));
    }

    private async Task EventLoop()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    EventPipeName(processId), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                eventReady.Set();
                await pipe.WaitForConnectionAsync(lifetime.Token)
                    .ConfigureAwait(false);
                using var reader = new BinaryReader(pipe, Encoding.Unicode, true);
                using var writer = new BinaryWriter(pipe, Encoding.Unicode, true);
                while (pipe.IsConnected && !lifetime.IsCancellationRequested)
                {
                    uint magic = reader.ReadUInt32();
                    uint version = reader.ReadUInt32();
                    int nameLength = checked((int)reader.ReadUInt32());
                    int dataLength = checked((int)reader.ReadUInt32());
                    if (magic != EventMagic || version != ProtocolVersion ||
                        nameLength < 0 || nameLength > 1_024 ||
                        dataLength < 0 || dataLength > 1_048_576)
                        throw new InvalidDataException(
                            "Invalid iStripper bridge event.");

                    string name = Encoding.Unicode.GetString(
                        reader.ReadBytes(nameLength));
                    byte[] data = reader.ReadBytes(dataLength);
                    bool skip = data.Length == dataLength &&
                        registryWrite(name, data);
                    writer.Write(skip ? 1 : 0);
                    writer.Flush();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) when (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(100, lifetime.Token).ConfigureAwait(false);
            }
        }
    }

    private void Inject(string bridgePath)
    {
        string fullPath = Path.GetFullPath(bridgePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException(
                "The iStripper playback bridge was not found.", fullPath);

        using Process target = Process.GetProcessById(processId);
        ProcessModule remoteKernel32 = target.Modules.Cast<ProcessModule>()
            .First(module => string.Equals(module.ModuleName, "kernel32.dll",
                StringComparison.OrdinalIgnoreCase));
        nint localKernel32 = GetModuleHandle("kernel32.dll");
        nint localLoadLibrary = GetProcAddress(localKernel32, "LoadLibraryW");
        nint remoteLoadLibrary = remoteKernel32.BaseAddress +
            (localLoadLibrary - localKernel32);

        nint process = OpenProcess(ProcessAccess, false, processId);
        if (process == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        byte[] path = Encoding.Unicode.GetBytes(fullPath + "\0");
        nint remotePath = 0;
        try
        {
            remotePath = VirtualAllocEx(process, 0, (nuint)path.Length,
                MemCommitReserve, PageReadWrite);
            if (remotePath == 0 ||
                !WriteProcessMemory(process, remotePath, path,
                    (nuint)path.Length, out nuint written) ||
                written != (nuint)path.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            nint thread = CreateRemoteThread(process, 0, 0,
                remoteLoadLibrary, remotePath, 0, out _);
            if (thread == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (WaitForSingleObject(thread, 10_000) != 0 ||
                    !GetExitCodeThread(thread, out uint module) || module == 0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(),
                        "Could not load the iStripper playback bridge.");
            }
            finally
            {
                CloseHandle(thread);
            }
        }
        finally
        {
            if (remotePath != 0)
                VirtualFreeEx(process, remotePath, 0, 0x8000);
            CloseHandle(process);
        }
    }

    private void DisconnectCommands()
    {
        commandReader?.Dispose();
        commandWriter?.Dispose();
        commandPipe?.Dispose();
        commandReader = null;
        commandWriter = null;
        commandPipe = null;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lock (commandLock)
            DisconnectCommands();
        try { eventTask.Wait(500); } catch { }
        eventReady.Dispose();
        lifetime.Dispose();
    }

    private static string CommandPipeName(int pid) =>
        $"IStripperQuickPlayer.Commands.{pid}";

    private static string EventPipeName(int pid) =>
        $"IStripperQuickPlayer.Events.{pid}";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle,
        int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(nint process, nint address,
        nuint size, uint allocationType, uint protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(nint process, nint address,
        nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(nint process, nint address,
        byte[] buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateRemoteThread(nint process,
        nint threadAttributes, nuint stackSize, nint startAddress,
        nint parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeThread(nint thread,
        out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint module, string name);
}
