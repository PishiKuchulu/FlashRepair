using System.Text.Json;
using FlashRepair.Core;

namespace FlashRepair.Backends.Virtual;

/// <summary>Fault kinds a virtual device can simulate. Used by tests and by the
/// `demo` command so the full diagnosis pipeline runs without hardware.</summary>
public enum VirtualFault
{
    None,
    RawFilesystem,          // partition exists, filesystem RAW, reads fine
    CorruptPartitionTable,  // no readable layout, reads fine
    ZeroCapacity,           // controller reports ~0 bytes (firmware/map suspect)
    FakeCapacity,           // declares big, real storage small; beyond real = zeros
    DeadRegions             // large parts of the surface fail to read
}

public sealed record VirtualDeviceSpec(
    string DeviceId,
    string Model,
    string Serial,
    long DeclaredCapacityBytes,
    long RealCapacityBytes,
    int VendorId,
    VirtualFault Fault,
    bool IsWriteProtected = false,
    bool IsSystemDisk = false);

/// <summary>File-backed virtual disks: capacity is logical, content is synthesized
/// (deterministic pattern in the "real" region, zeros beyond it), so multi-GB
/// fake-capacity devices cost no real storage.</summary>
public sealed class VirtualDiskBackend : IDiskBackend
{
    private readonly List<VirtualDeviceSpec> _specs;

    public string Name => "virtual";

    public VirtualDiskBackend(IEnumerable<VirtualDeviceSpec> specs) => _specs = specs.ToList();

    public static VirtualDiskBackend FromDirectory(string dir)
    {
        var specs = new List<VirtualDeviceSpec>();
        foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
        {
            var spec = JsonSerializer.Deserialize<VirtualDeviceSpec>(
                File.ReadAllText(file), JsonDefaults.Options);
            if (spec is not null) specs.Add(spec);
        }
        return new VirtualDiskBackend(specs);
    }

    public static VirtualDiskBackend BuiltInSamples() => new(new[]
    {
        new VirtualDeviceSpec("VIRT-HEALTHY-001", "SimFlash 32GB", "SIM20260001",
            32_000_000_000, 32_000_000_000, 0x13FE, VirtualFault.None),
        new VirtualDeviceSpec("VIRT-RAW-002", "SimFlash 16GB", "SIM20260002",
            16_000_000_000, 16_000_000_000, 0x090C, VirtualFault.RawFilesystem),
        new VirtualDeviceSpec("VIRT-ZERO-003", "SimFlash 64GB", "SIM20260003",
            0, 64_000_000_000, 0x058F, VirtualFault.ZeroCapacity),
        new VirtualDeviceSpec("VIRT-FAKE-004", "NoName 64GB", "SIM20260004",
            64_000_000_000, 8_000_000_000, 0xABCD, VirtualFault.FakeCapacity),
        new VirtualDeviceSpec("VIRT-DEAD-005", "OldFlash 8GB", "SIM20260005",
            8_000_000_000, 8_000_000_000, 0x1F75, VirtualFault.DeadRegions),
        new VirtualDeviceSpec("VIRT-NOLAYOUT-006", "SimFlash 32GB", "SIM20260006",
            32_000_000_000, 32_000_000_000, 0x0951, VirtualFault.CorruptPartitionTable),
    });

    private VirtualDeviceSpec Find(string deviceId) =>
        _specs.FirstOrDefault(s => s.DeviceId == deviceId)
        ?? throw new KeyNotFoundException("DEVICE_NOT_FOUND");

    private static DeviceRecord ToRecord(VirtualDeviceSpec s) => new(
        s.DeviceId, "Virtual", s.Model, s.Serial,
        s.Fault == VirtualFault.ZeroCapacity ? 0 : s.DeclaredCapacityBytes,
        "USB", IsRemovable: !s.IsSystemDisk, s.IsSystemDisk, s.IsWriteProtected,
        s.IsSystemDisk ? new[] { "C:" } : new[] { "E:" },
        $"virtual://{s.DeviceId}", s.VendorId, 0x5601);

    public Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DeviceRecord>>(_specs.Select(ToRecord).ToList());

    public Task<DeviceRecord?> GetDeviceAsync(string deviceId, CancellationToken ct = default) =>
        Task.FromResult(_specs.Any(s => s.DeviceId == deviceId) ? ToRecord(Find(deviceId)) : null);

    public Task<DiskLayoutInfo> GetLayoutAsync(string deviceId, CancellationToken ct = default)
    {
        var s = Find(deviceId);
        DiskLayoutInfo layout = s.Fault switch
        {
            VirtualFault.CorruptPartitionTable =>
                new DiskLayoutInfo("Raw", Array.Empty<PartitionInfo>(), s.DeclaredCapacityBytes),
            VirtualFault.ZeroCapacity =>
                new DiskLayoutInfo("None", Array.Empty<PartitionInfo>(), 0),
            _ => new DiskLayoutInfo("GPT",
                    new[] { new PartitionInfo(1, 1_048_576, s.DeclaredCapacityBytes - 1_048_576,
                        s.Fault == VirtualFault.RawFilesystem ? "" : "exFAT", "SIM") },
                    0)
        };
        return Task.FromResult(layout);
    }

    public Task<FilesystemProbe> ProbeFilesystemAsync(string deviceId, CancellationToken ct = default)
    {
        var s = Find(deviceId);
        FilesystemProbe probe = s.Fault switch
        {
            VirtualFault.RawFilesystem => new FilesystemProbe(FilesystemState.Raw,
                "Volume is RAW: Windows cannot read the filesystem, data region reads OK."),
            VirtualFault.CorruptPartitionTable => new FilesystemProbe(FilesystemState.Unknown,
                "No readable partition, filesystem cannot be probed."),
            VirtualFault.ZeroCapacity => new FilesystemProbe(FilesystemState.Unknown,
                "Device reports no capacity, filesystem cannot be probed."),
            _ => new FilesystemProbe(FilesystemState.Healthy, "exFAT volume mounts and reads cleanly.")
        };
        return Task.FromResult(probe);
    }

    public Task<byte[]> ReadRangeAsync(string deviceId, long offset, int length, CancellationToken ct = default)
    {
        var s = Find(deviceId);
        if (s.Fault == VirtualFault.DeadRegions && offset > s.RealCapacityBytes / 3)
            throw new IOException("READ_FAILED: simulated dead region");
        if (s.Fault == VirtualFault.ZeroCapacity)
            throw new IOException("READ_FAILED: device reports zero capacity");

        var buffer = new byte[length];
        for (var i = 0; i < length; i++)
        {
            var pos = offset + i;
            // Beyond the real capacity of a fake device: zeros (classic fake behaviour).
            buffer[i] = pos < s.RealCapacityBytes ? (byte)(pos % 251 + 1) : (byte)0;
        }
        return Task.FromResult(buffer);
    }
}
