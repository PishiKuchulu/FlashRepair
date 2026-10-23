namespace FlashRepair.Core;

/// <summary>
/// Local, versioned controller-identification catalog (spec section 9).
/// Phase 1 scope is deliberately honest: USB VID identifies the *brand/integrator*,
/// not the controller silicon. Entries therefore yield a *candidate family* with
/// modest confidence and the source is always recorded. Exact controller ID
/// requires vendor-specific interrogation (Phase 4) — the catalog never claims it.
/// </summary>
public static class ControllerCatalog
{
    public sealed record Entry(int VendorId, string Brand, string[] CandidateFamilies, double Confidence);

    public static readonly IReadOnlyList<Entry> Entries = new[]
    {
        new Entry(0x13FE, "Kingston / Phison-based", new[] { "Phison" }, 0.55),
        new Entry(0x090C, "Silicon Motion-based", new[] { "Silicon Motion" }, 0.55),
        new Entry(0x058F, "Alcor-based", new[] { "Alcor" }, 0.50),
        new Entry(0x1F75, "Innostor-based", new[] { "Innostor" }, 0.50),
        new Entry(0x0951, "Kingston", new[] { "Phison", "Silicon Motion" }, 0.35),
        new Entry(0x0781, "SanDisk", new[] { "SanDisk (proprietary)" }, 0.40),
        new Entry(0x8564, "Transcend", new[] { "Silicon Motion", "Alcor" }, 0.35),
        new Entry(0x125F, "ADATA", new[] { "Silicon Motion", "Phison" }, 0.35),
        new Entry(0xABCD, "Generic", new[] { "Unknown" }, 0.10),
    };

    public static ControllerGuess Guess(DeviceRecord device)
    {
        var entry = Entries.FirstOrDefault(e => e.VendorId == device.VendorId);
        if (entry is null)
            return new ControllerGuess("Unknown", 0.10,
                $"No catalog entry for VID 0x{device.VendorId:X4}; exact ID needs vendor interrogation (Phase 4).");
        return new ControllerGuess(
            string.Join(" / ", entry.CandidateFamilies),
            entry.Confidence,
            $"VID 0x{entry.VendorId:X4} = {entry.Brand}; brand-level indication only, not a silicon ID.");
    }
}

/// <summary>Firmware catalog (spec sections 7-A/11): metadata + hash only in v1.
/// Firmware binaries are NOT redistributed inside the installer; a candidate is
/// only ever returned for an exactly-matched, tested family record.</summary>
public sealed class FirmwareCatalog
{
    private readonly List<FirmwareCandidate> _items = new();

    public FirmwareCatalog(IEnumerable<FirmwareCandidate>? seed = null)
    {
        if (seed is not null) _items.AddRange(seed);
    }

    public IReadOnlyList<FirmwareCandidate> Lookup(string controllerFamily)
    {
        // Exact family token match only — "Phison / Silicon Motion" guesses never match.
        return _items
            .Where(f => string.Equals(f.ControllerFamily, controllerFamily, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
