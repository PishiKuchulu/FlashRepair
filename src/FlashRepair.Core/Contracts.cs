using System.Text.Json;

namespace FlashRepair.Core;

/// <summary>Risk ladder from the MVP spec, section 6. Higher = more dangerous.</summary>
public enum RiskLevel
{
    ReadOnly = 0,       // no writes to the device, free to run
    LowWrite = 1,       // reversible, low-risk writes (Phase 2)
    Destructive = 2,    // data loss, device survives (Phase 2, one approval)
    Firmware = 3        // possible permanent brick (Phase 4, two approvals)
}

public enum FaultCategory
{
    Healthy,
    FilesystemCorruption,
    PartitionTableDamage,
    WriteLock,
    FakeCapacity,
    FirmwareOrControllerSuspect,
    MediaWornOut,
    Unknown
}

public enum FilesystemState { Healthy, Corrupted, Raw, Unknown }

/// <summary>One physical device as the app sees it. Serial is the identity anchor:
/// the SafetyGate re-validates it at execution time for any non-read-only tool.</summary>
public sealed record DeviceRecord(
    string DeviceId,
    string Vendor,
    string Model,
    string Serial,
    long DeclaredCapacityBytes,
    string BusType,
    bool IsRemovable,
    bool IsSystemDisk,
    bool IsWriteProtected,
    IReadOnlyList<string> DriveLetters,
    string PhysicalPath,
    int VendorId = 0,
    int ProductId = 0);

public sealed record PartitionInfo(
    int Index, long OffsetBytes, long SizeBytes, string FileSystem, string Label);

public sealed record DiskLayoutInfo(
    string PartitionTableType, // "GPT" | "MBR" | "None" | "Raw"
    IReadOnlyList<PartitionInfo> Partitions,
    long UnallocatedBytes);

public sealed record FilesystemProbe(FilesystemState State, string Summary);

public sealed record ReadPoint(long OffsetBytes, bool Ok, bool AllZero, string? Error);

public sealed record ControllerGuess(string Family, double Confidence, string Source);

public sealed record FirmwareCandidate(
    string FileName, string Sha256, string ControllerFamily, string TestedStatus);

/// <summary>Standard tool output envelope (spec section 7). Data is always
/// structured and summarized — raw dumps never reach the model.</summary>
public sealed record ToolResult(bool Ok, string? ErrorCode, JsonElement Data, string Summary)
{
    public static ToolResult Success(object data, string summary)
    {
        var json = JsonSerializer.SerializeToElement(data, JsonDefaults.Options);
        return new ToolResult(true, null, json, summary);
    }

    public static ToolResult Fail(string errorCode, string summary) =>
        new(false, errorCode, JsonSerializer.SerializeToElement(new { }, JsonDefaults.Options), summary);
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions Pretty = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

/// <summary>Low-level, platform-specific device access. The Windows backend talks
/// to real hardware; the Virtual backend simulates known-fault devices so the
/// whole diagnosis pipeline is testable without a physical flash drive.</summary>
public interface IDiskBackend
{
    string Name { get; }
    Task<IReadOnlyList<DeviceRecord>> ListDevicesAsync(CancellationToken ct = default);
    Task<DeviceRecord?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
    Task<DiskLayoutInfo> GetLayoutAsync(string deviceId, CancellationToken ct = default);
    Task<FilesystemProbe> ProbeFilesystemAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Reads length bytes at offset. Implementations throw IOException
    /// for dead regions; callers (read-sample tool) convert that to a failed point.</summary>
    Task<byte[]> ReadRangeAsync(string deviceId, long offset, int length, CancellationToken ct = default);
}

/// <summary>Mutable per-session state shared through ToolContext. Steps are the
/// single source of truth for the final report — numbers come from tool
/// outputs recorded here, never from the model.</summary>
public sealed class SessionState
{
    public required string SessionId { get; init; }
    public string? DeviceId { get; set; }
    public bool Aborted { get; set; }
    public List<SessionStep> Steps { get; } = new();
    public List<ApprovalRecord> Approvals { get; } = new();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SessionStep(
    DateTimeOffset At, string ToolName, RiskLevel Risk, bool Ok, string Summary);

public sealed record ApprovalRecord(
    string ToolName, string DeviceId, string DeviceSerial, RiskLevel Risk,
    DateTimeOffset At, bool Granted);

public sealed class ToolContext
{
    public required IDiskBackend Backend { get; init; }
    public required SessionState Session { get; init; }
    public string AppDataDir { get; init; } = ".";
}

/// <summary>Closed tool contract (spec hard rule 1): the model may only pick
/// tools implementing this interface, with exactly these JSON schemas.</summary>
public interface ITool
{
    string Name { get; }
    RiskLevel Risk { get; }
    string Description { get; }
    /// <summary>JSON schema for the input object, advertised to the model.</summary>
    string InputSchemaJson { get; }
    Task<ToolResult> ExecuteAsync(JsonElement input, ToolContext ctx, CancellationToken ct = default);
}

public static class ToolInput
{
    public static string RequireDeviceId(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object &&
        input.TryGetProperty("device_id", out var v) &&
        v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!
            : throw new ToolInputException("DEVICE_ID_REQUIRED");

    public static string? OptionalString(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object &&
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}

public sealed class ToolInputException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
