using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace IStripperQuickPlayer.Interop;

public sealed class PlaybackBridgeClient : IDisposable
{
    public readonly record struct BufferCallTimings(
        double WriteMilliseconds,
        double CommandMilliseconds,
        double ReadBackMilliseconds);

    private const uint CommandMagic = 0x5142434D;
    private const uint EventMagic = 0x51424556;
    private const uint ShaderTextureMagic = 0x58545051;
    private const uint ProtocolVersion = 1;
    private const int ShaderTextureHeaderLength = 20;
    private const int MaximumShaderTextureDimension = 1024;
    private const int MaximumBridgeBufferLength =
        ShaderTextureHeaderLength +
        MaximumShaderTextureDimension * MaximumShaderTextureDimension * 4;
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
    private nint bufferProcess;
    private nint remoteBuffer;

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
        return CallBuffer(apiName, data, readBackLength: 0);
    }

    public int SetFullscreenShaderData(float[] values, out uint sequence)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != 4)
            throw new ArgumentException(
                "Exactly four shader values are required.", nameof(values));
        byte[] packet = CreateFullscreenShaderDataPacket(values);
        int result = CallBuffer(
            "IStripperSetFullscreenShaderData", packet,
            readBackLength: packet.Length);
        sequence = result < 0 ? 0 : BitConverter.ToUInt32(packet, 16);
        return result;
    }

    public int GetFullscreenShaderData(
        out float[] values, out uint sequence)
    {
        byte[] packet = new byte[20];
        int result = CallBuffer(
            "IStripperGetFullscreenShaderData", packet,
            readBackLength: packet.Length);
        values = result < 0 ? [] : Enumerable.Range(0, 4)
            .Select(index => BitConverter.ToSingle(packet, index * 4))
            .ToArray();
        sequence = result < 0 ? 0 : BitConverter.ToUInt32(packet, 16);
        return result;
    }

    public unsafe int SetFullscreenShaderTexture(int width, int height,
        byte[] rgba, out uint sequence)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width is < 1 or > MaximumShaderTextureDimension ||
            height is < 1 or > MaximumShaderTextureDimension ||
            rgba.Length != checked(width * height * 4))
        {
            throw new ArgumentException(
                "The shader texture must be a valid RGBA8 image.",
                nameof(rgba));
        }

        fixed (byte* pixels = rgba)
        {
            return SetFullscreenShaderTexture(width, height,
                (nint)pixels, rgba.Length, out sequence);
        }
    }

    public int SetFullscreenShaderTexture(int width, int height,
        nint rgba, int byteLength, out uint sequence)
    {
        return SetFullscreenShaderTexture(width, height, rgba,
            byteLength, out sequence, out _);
    }

    public int SetFullscreenShaderTexture(int width, int height,
        nint rgba, int byteLength, out uint sequence,
        out BufferCallTimings timings)
    {
        if (width is < 1 or > MaximumShaderTextureDimension ||
            height is < 1 or > MaximumShaderTextureDimension ||
            rgba == 0 || byteLength != checked(width * height * 4))
        {
            throw new ArgumentException(
                "The shader texture must be a valid RGBA8 image.",
                nameof(rgba));
        }

        byte[] header = CreateFullscreenShaderTextureHeader(
            width, height, byteLength);
        int result = CallBuffer(
            "IStripperSetFullscreenShaderTexture", header,
            readBackLength: ShaderTextureHeaderLength, out timings,
            payload: rgba, payloadLength: byteLength);
        sequence = result < 0 ? 0 : BitConverter.ToUInt32(header, 12);
        return result;
    }

    public int GetFullscreenShaderTexture(
        out int width, out int height, out uint sequence)
    {
        byte[] packet = new byte[ShaderTextureHeaderLength];
        int result = CallBuffer(
            "IStripperGetFullscreenShaderTexture", packet,
            readBackLength: packet.Length);
        bool valid = result >= 0 &&
            BitConverter.ToUInt32(packet, 0) == ShaderTextureMagic;
        width = valid ? checked((int)BitConverter.ToUInt32(packet, 4)) : 0;
        height = valid ? checked((int)BitConverter.ToUInt32(packet, 8)) : 0;
        sequence = valid ? BitConverter.ToUInt32(packet, 12) : 0;
        return valid ? result : result < 0 ? result : unchecked((int)0x80004005);
    }

    private int CallBuffer(string apiName, byte[] data, int readBackLength,
        nint payload = 0, int payloadLength = 0)
    {
        return CallBuffer(apiName, data, readBackLength, out _,
            payload, payloadLength);
    }

    private int CallBuffer(string apiName, byte[] data, int readBackLength,
        out BufferCallTimings timings,
        nint payload = 0, int payloadLength = 0)
    {
        timings = default;
        ArgumentNullException.ThrowIfNull(data);
        int totalLength = checked(data.Length + payloadLength);
        if (data.Length == 0 || totalLength > MaximumBridgeBufferLength)
            throw new ArgumentOutOfRangeException(nameof(data));
        if (readBackLength < 0 || readBackLength > data.Length)
            throw new ArgumentOutOfRangeException(nameof(readBackLength));
        if (payloadLength < 0 || (payloadLength > 0) != (payload != 0))
            throw new ArgumentOutOfRangeException(nameof(payloadLength));

        lock (commandLock)
        {
            try
            {
                long writeStarted = Stopwatch.GetTimestamp();
                EnsureRemoteBuffer();
                if (!WriteProcessMemory(bufferProcess, remoteBuffer, data,
                    (nuint)data.Length, out nuint written) ||
                    written != (nuint)data.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (payloadLength > 0 &&
                    (!WriteProcessMemoryPointer(bufferProcess,
                        remoteBuffer + data.Length, payload,
                        (nuint)payloadLength, out written) ||
                     written != (nuint)payloadLength))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                long commandStarted = Stopwatch.GetTimestamp();
                int result = Call(apiName,
                    (ulong)(nuint)remoteBuffer);
                long readBackStarted = Stopwatch.GetTimestamp();
                if (result >= 0 && readBackLength > 0 &&
                    (!ReadProcessMemory(bufferProcess, remoteBuffer, data,
                        (nuint)readBackLength, out nuint read) ||
                     read != (nuint)readBackLength))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                long completed = Stopwatch.GetTimestamp();
                timings = new(
                    Stopwatch.GetElapsedTime(
                        writeStarted, commandStarted).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(
                        commandStarted, readBackStarted).TotalMilliseconds,
                    Stopwatch.GetElapsedTime(
                        readBackStarted, completed).TotalMilliseconds);
                return result;
            }
            catch
            {
                ReleaseRemoteBuffer();
                throw;
            }
        }
    }

    private void EnsureRemoteBuffer()
    {
        if (bufferProcess != 0 && remoteBuffer != 0)
            return;
        bufferProcess = OpenProcess(ProcessAccess, false, processId);
        if (bufferProcess == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        remoteBuffer = VirtualAllocEx(bufferProcess, 0,
            (nuint)MaximumBridgeBufferLength,
            MemCommitReserve, PageReadWrite);
        if (remoteBuffer == 0)
        {
            int error = Marshal.GetLastWin32Error();
            CloseHandle(bufferProcess);
            bufferProcess = 0;
            throw new Win32Exception(error);
        }
    }

    private void ReleaseRemoteBuffer()
    {
        if (remoteBuffer != 0 && bufferProcess != 0)
            VirtualFreeEx(bufferProcess, remoteBuffer, 0, 0x8000);
        remoteBuffer = 0;
        if (bufferProcess != 0)
            CloseHandle(bufferProcess);
        bufferProcess = 0;
    }

    public int StartRegistryHook() => Call("IStripperStartRegistryHook");

    internal static bool VerifyProtocol()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            WriteCommand(writer, Encoding.UTF8.GetBytes("Test"), 42);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        float[] shaderValues = [12.5f, 3f, -0.75f, 900f];
        byte[] shaderPacket = CreateFullscreenShaderDataPacket(shaderValues);
        byte[] texturePixels = [255, 0, 0, 255, 0, 255, 0, 255];
        byte[] texturePacket = CreateFullscreenShaderTexturePacket(
            2, 1, texturePixels);
        return reader.ReadUInt32() == CommandMagic &&
            reader.ReadUInt32() == ProtocolVersion &&
            reader.ReadUInt32() == 4 &&
            reader.ReadUInt32() == 1 &&
            reader.ReadUInt64() == 42 &&
            Encoding.UTF8.GetString(reader.ReadBytes(4)) == "Test" &&
            shaderPacket.Length == 20 &&
            Enumerable.Range(0, 4).All(index =>
                BitConverter.ToSingle(shaderPacket, index * 4) ==
                    shaderValues[index]) &&
            BitConverter.ToUInt32(shaderPacket, 16) == 0 &&
            texturePacket.Length == ShaderTextureHeaderLength + 8 &&
            BitConverter.ToUInt32(texturePacket, 0) == ShaderTextureMagic &&
            BitConverter.ToUInt32(texturePacket, 4) == 2 &&
            BitConverter.ToUInt32(texturePacket, 8) == 1 &&
            BitConverter.ToUInt32(texturePacket, 12) == 0 &&
            BitConverter.ToUInt32(texturePacket, 16) == 8 &&
            texturePacket.AsSpan(ShaderTextureHeaderLength)
                .SequenceEqual(texturePixels);
    }

    private static byte[] CreateFullscreenShaderDataPacket(float[] values)
    {
        byte[] packet = new byte[20];
        for (int index = 0; index < 4; index++)
            BitConverter.GetBytes(values[index]).CopyTo(packet, index * 4);
        return packet;
    }

    private static byte[] CreateFullscreenShaderTexturePacket(
        int width, int height, byte[] rgba)
    {
        byte[] packet = new byte[checked(ShaderTextureHeaderLength +
            rgba.Length)];
        CreateFullscreenShaderTextureHeader(width, height, rgba.Length)
            .CopyTo(packet, 0);
        rgba.CopyTo(packet, ShaderTextureHeaderLength);
        return packet;
    }

    private static byte[] CreateFullscreenShaderTextureHeader(
        int width, int height, int byteLength)
    {
        byte[] packet = new byte[ShaderTextureHeaderLength];
        BitConverter.GetBytes(ShaderTextureMagic).CopyTo(packet, 0);
        BitConverter.GetBytes((uint)width).CopyTo(packet, 4);
        BitConverter.GetBytes((uint)height).CopyTo(packet, 8);
        BitConverter.GetBytes(0u).CopyTo(packet, 12);
        BitConverter.GetBytes((uint)byteLength).CopyTo(packet, 16);
        return packet;
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
        BinaryReader? reader = commandReader;
        BinaryWriter? writer = commandWriter;
        NamedPipeClientStream? pipe = commandPipe;
        commandReader = null;
        commandWriter = null;
        commandPipe = null;
        try { reader?.Dispose(); } catch { }
        try { writer?.Dispose(); } catch { }
        try { pipe?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        lifetime.Cancel();
        lock (commandLock)
        {
            ReleaseRemoteBuffer();
            DisconnectCommands();
        }
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

    [DllImport("kernel32.dll", SetLastError = true,
        EntryPoint = "WriteProcessMemory")]
    private static extern bool WriteProcessMemoryPointer(nint process,
        nint address, nint buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(nint process, nint address,
        byte[] buffer, nuint size, out nuint read);

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
