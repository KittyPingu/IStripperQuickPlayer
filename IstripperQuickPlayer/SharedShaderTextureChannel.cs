using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace IStripperQuickPlayer;

internal sealed unsafe class SharedShaderTextureChannel : IDisposable
{
    public const uint Magic = 0x4D545051; // QPTM
    public const uint ProtocolVersion = 1;
    public const int HeaderLength = 64;
    public const int SlotCount = 2;
    public const int MaximumDimension = 1024;
    public const int SlotCapacity =
        MaximumDimension * MaximumDimension * 4;
    public const long MappingLength =
        HeaderLength + (long)SlotCount * SlotCapacity;

    public const int MagicOffset = 0;
    public const int VersionOffset = 4;
    public const int HeaderLengthOffset = 8;
    public const int SlotCountOffset = 12;
    public const int SlotCapacityOffset = 16;
    public const int PublishedSlotOffset = 20;
    public const int WidthOffset = 24;
    public const int HeightOffset = 28;
    public const int ByteLengthOffset = 32;
    public const int ProducerSequenceOffset = 36;
    public const int PublishedGenerationOffset = 40;
    public const int ConsumedGenerationOffset = 48;

    private readonly Func<int, int, nint, int, bool> apply;
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly EventWaitHandle updatedEvent;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task worker;
    private byte* viewPointer;
    private byte* buffer;
    private bool disposed;

    public SharedShaderTextureChannel(
        Func<int, int, nint, int, bool> apply)
    {
        this.apply = apply;
        string suffix = $"{Environment.ProcessId}." +
            Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        MappingName = $"Local\\IStripperQuickPlayer.Texture.{suffix}";
        EventName = $"Local\\IStripperQuickPlayer.TextureUpdated.{suffix}";

        mapping = MemoryMappedFile.CreateNew(
            MappingName, MappingLength,
            MemoryMappedFileAccess.ReadWrite);
        view = mapping.CreateViewAccessor(
            0, MappingLength, MemoryMappedFileAccess.ReadWrite);
        byte* pointer = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        viewPointer = pointer + view.PointerOffset;
        buffer = (byte*)NativeMemory.Alloc((nuint)SlotCapacity);
        if (buffer == null)
            throw new OutOfMemoryException();

        updatedEvent = new EventWaitHandle(false,
            EventResetMode.AutoReset, EventName, out _);
        InitializeHeader();
        worker = Task.Run(WorkerLoop);
    }

    public string MappingName { get; }
    public string EventName { get; }

    public object Description => new
    {
        protocolVersion = ProtocolVersion,
        mappingName = MappingName,
        eventName = EventName,
        mappingLength = MappingLength,
        headerLength = HeaderLength,
        slotCount = SlotCount,
        slotCapacity = SlotCapacity,
        maximumWidth = MaximumDimension,
        maximumHeight = MaximumDimension,
        format = "rgba8",
        orientation = "top-left",
        offsets = new
        {
            magic = MagicOffset,
            version = VersionOffset,
            headerLength = HeaderLengthOffset,
            slotCount = SlotCountOffset,
            slotCapacity = SlotCapacityOffset,
            publishedSlot = PublishedSlotOffset,
            width = WidthOffset,
            height = HeightOffset,
            byteLength = ByteLengthOffset,
            producerSequence = ProducerSequenceOffset,
            publishedGeneration = PublishedGenerationOffset,
            consumedGeneration = ConsumedGenerationOffset,
            pixels = HeaderLength
        }
    };

    private void InitializeHeader()
    {
        new Span<byte>(viewPointer, HeaderLength).Clear();
        WriteUInt32(MagicOffset, Magic);
        WriteUInt32(VersionOffset, ProtocolVersion);
        WriteUInt32(HeaderLengthOffset, HeaderLength);
        WriteUInt32(SlotCountOffset, SlotCount);
        WriteUInt32(SlotCapacityOffset, SlotCapacity);
        WriteUInt32(PublishedSlotOffset, uint.MaxValue);
        Thread.MemoryBarrier();
    }

    private void WorkerLoop()
    {
        WaitHandle[] handles =
            [updatedEvent, lifetime.Token.WaitHandle];
        while (!lifetime.IsCancellationRequested)
        {
            if (WaitHandle.WaitAny(handles) != 0)
                return;

            try
            {
                while (!lifetime.IsCancellationRequested &&
                    TryCopyLatest(out int width, out int height,
                        out int byteLength, out long generation))
                {
                    if (apply(width, height, (nint)buffer, byteLength))
                        Volatile.Write(
                            ref Int64At(ConsumedGenerationOffset),
                            generation);

                    if (Volatile.Read(ref Int64At(
                            PublishedGenerationOffset)) == generation)
                        break;
                }
            }
            catch when (!lifetime.IsCancellationRequested)
            {
                // A bridge disconnect fails this frame. A later publication
                // wakes the worker and retries against the reattached bridge.
            }
        }
    }

    private bool TryCopyLatest(out int width, out int height,
        out int byteLength, out long generation)
    {
        width = 0;
        height = 0;
        byteLength = 0;
        generation = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            long before = Volatile.Read(
                ref Int64At(PublishedGenerationOffset));
            if (before <= 0 || (before & 1) != 0)
            {
                Thread.Yield();
                continue;
            }
            if (before == Volatile.Read(
                    ref Int64At(ConsumedGenerationOffset)))
                return false;

            uint slot = ReadUInt32(PublishedSlotOffset);
            uint frameWidth = ReadUInt32(WidthOffset);
            uint frameHeight = ReadUInt32(HeightOffset);
            uint frameByteLength = ReadUInt32(ByteLengthOffset);
            if (slot >= SlotCount || frameWidth == 0 ||
                frameWidth > MaximumDimension || frameHeight == 0 ||
                frameHeight > MaximumDimension ||
                frameByteLength != frameWidth * frameHeight * 4 ||
                frameByteLength > SlotCapacity)
                return false;

            byte* source = viewPointer + HeaderLength +
                slot * SlotCapacity;
            Buffer.MemoryCopy(source, buffer, SlotCapacity,
                frameByteLength);
            Thread.MemoryBarrier();
            long after = Volatile.Read(
                ref Int64At(PublishedGenerationOffset));
            if (before != after)
                continue;

            width = (int)frameWidth;
            height = (int)frameHeight;
            byteLength = (int)frameByteLength;
            FlipRows(new Span<byte>(buffer, byteLength),
                width, height);
            generation = after;
            return true;
        }
        return false;
    }

    private static void FlipRows(
        Span<byte> rgba, int width, int height)
    {
        int rowLength = checked(width * 4);
        Span<byte> row = stackalloc byte[rowLength];
        for (int top = 0; top < height / 2; top++)
        {
            int bottom = height - 1 - top;
            Span<byte> topRow = rgba.Slice(top * rowLength, rowLength);
            Span<byte> bottomRow = rgba.Slice(
                bottom * rowLength, rowLength);
            topRow.CopyTo(row);
            bottomRow.CopyTo(topRow);
            row.CopyTo(bottomRow);
        }
    }

    private uint ReadUInt32(int offset) =>
        *(uint*)(viewPointer + offset);

    private void WriteUInt32(int offset, uint value) =>
        *(uint*)(viewPointer + offset) = value;

    private ref long Int64At(int offset) =>
        ref *(long*)(viewPointer + offset);

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        lifetime.Cancel();
        updatedEvent.Set();
        try { worker.Wait(5_000); } catch { }
        updatedEvent.Dispose();
        lifetime.Dispose();
        if (buffer != null)
        {
            NativeMemory.Free(buffer);
            buffer = null;
        }
        if (viewPointer != null)
        {
            viewPointer = null;
            view.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        view.Dispose();
        mapping.Dispose();
    }
}
