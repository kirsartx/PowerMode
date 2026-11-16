using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;

namespace PowerModeWinUI;

/// <summary>
/// Identity of the running main window. A second launch may activate this handle
/// only after the process check passes; a window titled "PowerMode" is not enough.
/// </summary>
internal readonly record struct PublishedInstanceWindow(uint Magic, uint ProcessId, long WindowHandle)
{
    public const uint ExpectedMagic = 0x504D5749;
    public const int Size = 16;

    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0), Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), ProcessId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), WindowHandle);
        return bytes;
    }

    public static bool TryRead(ReadOnlySpan<byte> bytes, out PublishedInstanceWindow window)
    {
        window = default;
        if (bytes.Length < Size)
            return false;
        window = new(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]));
        return true;
    }
}

internal static class SingleInstanceWindowSelector
{
    public static bool TrySelect(
        PublishedInstanceWindow published,
        uint currentProcessId,
        Func<long, uint, bool> windowBelongsToProcess,
        out long windowHandle)
    {
        ArgumentNullException.ThrowIfNull(windowBelongsToProcess);
        windowHandle = 0;
        if (published.Magic != PublishedInstanceWindow.ExpectedMagic ||
            published.ProcessId == 0 ||
            published.ProcessId == currentProcessId ||
            published.WindowHandle == 0 ||
            !windowBelongsToProcess(published.WindowHandle, published.ProcessId))
        {
            return false;
        }

        windowHandle = published.WindowHandle;
        return true;
    }
}

internal static class SingleInstanceWindowBridge
{
    public const string MappingName = @"Local\PowerMode.WinUI3.SingleInstance.Window";

    public static MemoryMappedFile Publish(IntPtr windowHandle, string? mappingName = null)
    {
        var map = MemoryMappedFile.CreateNew(mappingName ?? MappingName, PublishedInstanceWindow.Size);
        try
        {
            Write(map, new PublishedInstanceWindow(
                PublishedInstanceWindow.ExpectedMagic,
                (uint)Environment.ProcessId,
                windowHandle.ToInt64()));
            return map;
        }
        catch
        {
            map.Dispose();
            throw;
        }
    }

    public static bool TryRead(out PublishedInstanceWindow published, string? mappingName = null)
    {
        published = default;
        try
        {
            using var map = MemoryMappedFile.OpenExisting(mappingName ?? MappingName);
            using var view = map.CreateViewAccessor(0, PublishedInstanceWindow.Size, MemoryMappedFileAccess.Read);
            var bytes = new byte[PublishedInstanceWindow.Size];
            if (view.ReadArray(0, bytes, 0, bytes.Length) != bytes.Length ||
                !PublishedInstanceWindow.TryRead(bytes, out published))
            {
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or UnauthorizedAccessException
            or IOException)
        {
            return false;
        }
    }

    public static bool BelongsToProcess(long windowHandle, uint processId)
    {
        var handle = new IntPtr(windowHandle);
        if (handle == IntPtr.Zero || !IsWindow(handle))
            return false;
        _ = GetWindowThreadProcessId(handle, out var owner);
        return owner == processId;
    }

    private static void Write(MemoryMappedFile map, PublishedInstanceWindow window)
    {
        using var view = map.CreateViewAccessor(0, PublishedInstanceWindow.Size, MemoryMappedFileAccess.Write);
        var bytes = window.ToBytes();
        view.WriteArray(0, bytes, 0, bytes.Length);
        view.Flush();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
