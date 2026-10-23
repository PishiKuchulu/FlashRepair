using FlashRepair.Core;

namespace FlashRepair.SafetyGate;

public sealed record GateDecision(bool Allowed, string? DenyReason)
{
    public static GateDecision Allow() => new(true, null);
    public static GateDecision Deny(string reason) => new(false, reason);
}

public sealed class SafetyGateOptions
{
    /// <summary>Phase 1 ships read-only: anything above ReadOnly is denied at the
    /// gate regardless of approvals. Phase 2 flips this on per risk level.</summary>
    public bool AllowAboveReadOnly { get; init; } = false;
    public RiskLevel MaxAllowedRisk { get; init; } = RiskLevel.ReadOnly;
}

/// <summary>
/// The non-bypassable checkpoint every tool execution passes through
/// (spec section 5). Code-enforced, not prompt-enforced:
/// 1) system disks are never a destructive target;
/// 2) risk >= Destructive needs a recorded approval for the same tool AND the
///    same device serial, re-validated at execution time;
/// 3) Phase 1 mode denies everything above the configured max risk.
/// </summary>
public sealed class SafetyGate(SafetyGateOptions? options = null)
{
    private readonly SafetyGateOptions _options = options ?? new SafetyGateOptions();

    public GateDecision Evaluate(ITool tool, DeviceRecord? device, SessionState session)
    {
        if (tool.Risk == RiskLevel.ReadOnly)
            return GateDecision.Allow();

        if (!_options.AllowAboveReadOnly || tool.Risk > _options.MaxAllowedRisk)
            return GateDecision.Deny(
                $"Tool '{tool.Name}' has risk {tool.Risk}; this build allows up to {_options.MaxAllowedRisk}.");

        if (device is null)
            return GateDecision.Deny("A target device is required for non-read-only tools.");

        if (device.IsSystemDisk)
            return GateDecision.Deny("System disks are permanently excluded as targets.");

        if (tool.Risk >= RiskLevel.Destructive)
        {
            var approval = session.Approvals.LastOrDefault(a =>
                a.Granted && a.ToolName == tool.Name && a.DeviceId == device.DeviceId);
            if (approval is null)
                return GateDecision.Deny($"No recorded approval for '{tool.Name}' on this device.");
            if (!string.Equals(approval.DeviceSerial, device.Serial, StringComparison.Ordinal))
                return GateDecision.Deny(
                    "Device serial changed since approval — the approved device is not the connected device.");
        }
        return GateDecision.Allow();
    }

    /// <summary>Records a user approval. Called only from UI/CLI user actions —
    /// deliberately not exposed as a model tool.</summary>
    public static void GrantApproval(SessionState session, string toolName, DeviceRecord device, RiskLevel risk) =>
        session.Approvals.Add(new ApprovalRecord(
            toolName, device.DeviceId, device.Serial, risk, DateTimeOffset.UtcNow, Granted: true));
}
