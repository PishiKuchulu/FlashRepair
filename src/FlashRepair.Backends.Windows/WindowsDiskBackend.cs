using System.Runtime.InteropServices;
using FlashRepair.Core;
using Microsoft.Win32.SafeHandles;

namespace FlashRepair.Backends.Windows;

/// <summary>
/// Real-hardware backend for Windows (Phase 1: read-only operations only).
/// Talks to \\.\PhysicalDriveN with DeviceIoControl. Compiles on any OS
/// (P/Invoke declarations are inert until called); every public method
/// hard-fails off Windows. No write IOCTL exists in this class by design —
/// Phase 2 write tools will live behind the SafetyGate in their own backend.
/// </summary>
public sealed class WindowsDiskBackend : IDiskBackend
{
    public string Name => "windows";

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint IoctlDiskGetLengthInfo = 0x0007405C;
    private const uint IoctlDiskGetDriveLayoutEx = 0x00070050;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? inBuffer,
        uint inSize, byte[] outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distance,
        out long newPointer, uint moveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint toRead,
        out uint read, IntPtr overlapped);

    public WindowsDiskBackend()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "WindowsDiskBackend requires Windows. Use the virtual backend elsewhere.");
    }

    private static SafeFileHandle OpenDrive(int index)
    {
        var handle = CreateFile($@"\\.\PhysicalDrive{index}", GenericRead,
            FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException($"Cannot open PhysicalDrive{index} (run as admin?).");
        return handle;
    }

    private sealed record DriveProbe(int Index, DeviceRecord Record);

    private List<DriveProbe> ProbeAll()
    {
        var probes = new List<DriveProbe>();
        for (var i = 0; i < 32; i++)
        {
            try
            {
                using var handle = OpenDrive(i);
                // STORAGE_PROPERTY_QUERY: PropertyId=0 (StorageDeviceProperty), QueryType=0
                var query = new byte[12];
                var descriptor = new byte[1024];
                if (!DeviceIoControl(handle, IoctlStorageQueryProperty, query, (uint)query.Length,
                        descriptor, (uint)descriptor.Length, out _, IntPtr.Zero))
                    continue;

                string ReadString(uint offset) =>
                    offset == 0 || offset >= descriptor.Length
                        ? ""
                        : System.Text.Encoding.ASCII.GetString(descriptor, (int)offset,
                            Array.IndexOf(descriptor, (byte)0, (int)offset) - (int)offset);

                var deviceType = descriptor[4];
                var removable = descriptor[8] != 0;
                var vendor = ReadString(BitConverter.ToUInt32(descriptor, 12)).Trim();
                var product = ReadString(BitConverter.ToUInt32(descriptor, 16)).Trim();
                var serial = ReadString(BitConverter.ToUInt32(descriptor, 24)).Trim();
                var busType = BitConverter.ToInt32(descriptor, 28) switch
                {
                    7 => "USB", 17 => "NVMe", 11 => "SATA", _ => "Other"
                };
                _ = deviceType;

                var lengthBuffer = new byte[8];
                long length = DeviceIoControl(handle, IoctlDiskGetLengthInfo, null, 0,
                    lengthBuffer, (uint)lengthBuffer.Length, out _, IntPtr.Zero)
                    ? BitConverter.ToInt64(lengthBuffer, 0)
                    : 0;

                // A fixed internal disk holding the system volume is marked as system disk.
                var isSystem = !removable && i == 0;

                var record = new DeviceRecord(
                    $"PHYS{i}-{serial}", vendor, product, serial, length, busType,
                    removable, isSystem, false,
                    DriveLettersFor(i), $@"\\.\PhysicalDrive{i}");
                probes.Add(new DriveProbe(i, record));
            }
            catch (IOException)
            {
                // Drive slot absent or inaccessible — normal during enumeration.
            }
        }
        return probes;
    }

    private static IReadOnlyList<string> DriveLettersFor(int physicalIndex)
    {
        // Letter <-> physical mapping via volume extents is Phase 1.5 work; for
        // Phase 1 we return letters whose DriveInfo is removable for removable
        // disks and none otherwise, and the layout probe reports honestly.
        var letters = new List<string>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType == DriveType.Removable)
                    letters.Add(drive.Name.TrimEnd('\\'));
            }
            catch (IOException) { /* unreadable volumes are exactly our use case */ }
        }
        return physicalIndex == 0 ? letters.Take(1).ToList() : letters;
    }

    public Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeviceRecord>>(
            ProbeAll().Select(p => p.Record).ToList());

    public Task<DeviceRecord?> GetDeviceAsync(string deviceId, CancellationToken ct = default) =>
        Task.FromResult(ProbeAll().Select(p => p.Record)
            .FirstOrDefault(r => r.DeviceId == deviceId));

    private DriveProbe FindProbe(string deviceId) =>
        ProbeAll().FirstOrDefault(p => p.Record.DeviceId == deviceId)
        ?? throw new KeyNotFoundException("DEVICE_NOT_FOUND");

    public Task<DiskLayoutInfo> GetLayoutAsync(string deviceId, CancellationToken ct = default)
    {
        var probe = FindProbe(deviceId);
        using var handle = OpenDrive(probe.Index);
        var buffer = new byte[8192];
        if (!DeviceIoControl(handle, IoctlDiskGetDriveLayoutEx, null, 0, buffer,
                (uint)buffer.Length, out _, IntPtr.Zero))
            return Task.FromResult(new DiskLayoutInfo("Raw",
                Array.Empty<PartitionInfo>(), probe.Record.DeclaredCapacityBytes));

        var style = BitConverter.ToInt32(buffer, 0); // 0=MBR, 1=GPT, 2=RAW
        var count = BitConverter.ToInt32(buffer, 4);
        var partitions = new List<PartitionInfo>();
        var offset = 48; // after DRIVE_LAYOUT_INFORMATION_EX header (GPT/MBR union)
        for (var i = 0; i < count && offset + 144 <= buffer.Length; i++)
        {
            var pStyle = BitConverter.ToInt32(buffer, offset);
            var starting = BitConverter.ToInt64(buffer, offset + 8);
            var length = BitConverter.ToInt64(buffer, offset + 16);
            partitions.Add(new PartitionInfo(i + 1, starting, length,
                pStyle == 1 ? "GPT-partition" : "MBR-partition", ""));
            offset += 144; // sizeof(PARTITION_INFORMATION_EX) on x64
        }
        var styleName = style switch { 0 => "MBR", 1 => "GPT", _ => "Raw" };
        return Task.FromResult(new DiskLayoutInfo(styleName, partitions, 0));
    }

    public Task<FilesystemProbe> ProbeFilesystemAsync(string deviceId, CancellationToken ct = default)
    {
        var probe = FindProbe(deviceId);
        foreach (var letter in probe.Record.DriveLetters)
        {
            try
            {
                var info = new DriveInfo(letter);
                var format = info.DriveFormat; // throws for RAW/unformatted volumes
                return Task.FromResult(new FilesystemProbe(FilesystemState.Healthy,
                    $"{letter} mounts as {format}."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(new FilesystemProbe(FilesystemState.Raw,
                    $"{letter} cannot be read as a filesystem (RAW or corrupt): {ex.GetType().Name}."));
            }
        }
        return Task.FromResult(new FilesystemProbe(FilesystemState.Unknown,
            "No drive letter mapped; filesystem state unknown from Phase 1 probe."));
    }

    public Task<byte[]> ReadRangeAsync(string deviceId, long offset, int length, CancellationToken ct = default)
    {
        var probe = FindProbe(deviceId);
        using var handle = OpenDrive(probe.Index);
        if (!SetFilePointerEx(handle, offset, out _, 0))
            throw new IOException($"Seek to {offset} failed.");
        var buffer = new byte[length];
        if (!ReadFile(handle, buffer, (uint)length, out var read, IntPtr.Zero) || read == 0)
            throw new IOException($"Read at {offset} failed.");
        return Task.FromResult(buffer.AsSpan(0, (int)read).ToArray());
    }
}
