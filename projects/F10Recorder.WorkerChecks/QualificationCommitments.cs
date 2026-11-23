namespace F10Recorder;

// Conservative future commitments stay booked through confirmed case closure.
// Initial admission also allows the sequential active case and shared reserves
// to materialize before their future commitment can be retired. Native runtime
// observations remain authoritative; this forecast grants no allocation credit.
internal sealed class QualificationCommitments
{
    readonly HashSet<string> admitted;
    readonly HashSet<string> sealedCases = new(StringComparer.Ordinal);
    readonly long caseBytes, sharedBytes, scratchBytes;
    internal QualificationCommitments(IEnumerable<string> labels, long sourceBytes, long outputBytes, long copyBytes,
        long scratchBytes = 1_256_000_000L, long traceBytes = 67_108_864L, long namespaceBytes = 0)
    {
        var ordered = labels.ToArray(); admitted = new(ordered, StringComparer.Ordinal);
        if (ordered.Length != admitted.Count || ordered.Any(string.IsNullOrWhiteSpace)
            || sourceBytes < 0 || outputBytes < 0 || copyBytes < 0 || scratchBytes < 0 || namespaceBytes < 0 || traceBytes is < 1 or > 3221225472L)
            throw new IOException("Invalid finite qualification commitments.");
        caseBytes = checked(sourceBytes + outputBytes);
        this.scratchBytes = scratchBytes;
        sharedBytes = checked(copyBytes + scratchBytes + 64_000_000L + 256_000_000L + traceBytes + namespaceBytes);
    }
    internal long RemainingBytes => checked(sharedBytes + (admitted.Count - sealedCases.Count) * caseBytes);
    internal long InitialRequiredFreeBytes(long floorBytes)
        => checked(RuntimeRequiredFreeBytes(floorBytes) + (admitted.Count == 0 ? 0 : caseBytes) + sharedBytes);
    internal long RuntimeRequiredFreeBytes(long floorBytes, long nextBytes = 0)
    {
        if (floorBytes < 0 || nextBytes < 0) throw new IOException("Invalid qualification capacity demand.");
        return checked(floorBytes + Math.Max(RemainingBytes, checked(nextBytes + scratchBytes)));
    }
    internal void Seal(string label)
    {
        if (!admitted.Contains(label)) throw new IOException("Cannot seal an unadmitted qualification case.");
        sealedCases.Add(label); // Repeated closure never credits the case twice.
    }
    internal static int Checks()
    {
        void Check(bool value) { if (!value) throw new IOException("Qualification commitment regression."); }
        var full = new QualificationCommitments(["T01", "T02", "T03", "H02", "H01", "H03"], 1_000_000_000, 2_000_000_000, 1_000_000_000);
        Check(full.RemainingBytes == 20_643_108_864);
        const long floor = 60_000_000_000L;
        Check(full.InitialRequiredFreeBytes(floor) == 86_286_217_728L);
        Check(80_643_108_864L < full.InitialRequiredFreeBytes(floor));
        Check(full.InitialRequiredFreeBytes(floor) - 1 < 86_286_217_728L);
        long free = full.InitialRequiredFreeBytes(floor) - 2_643_108_864L;
        foreach (string label in new[] { "T01", "T02", "T03", "H02", "H01", "H03" })
        {
            free -= 3_000_000_000L;
            Check(free == full.RuntimeRequiredFreeBytes(floor));
            Check(free - 1 < full.RuntimeRequiredFreeBytes(floor));
            full.Seal(label);
            Check(free >= full.RuntimeRequiredFreeBytes(floor));
        }
        Check(full.RemainingBytes == 2_643_108_864L);
        Check(full.RuntimeRequiredFreeBytes(floor, 5_000_000_000L) == 66_256_000_000L);
        Check(floor - 1 < full.RuntimeRequiredFreeBytes(floor));
        bool overflow = false; try { _ = full.InitialRequiredFreeBytes(long.MaxValue); } catch (OverflowException) { overflow = true; } Check(overflow);
        full = new QualificationCommitments(["T01", "T02", "T03", "H02", "H01", "H03"], 1_000_000_000, 2_000_000_000, 1_000_000_000);
        full.Seal("T01"); Check(full.RemainingBytes == 17_643_108_864);
        full.Seal("T01"); Check(full.RemainingBytes == 17_643_108_864);
        var prefix = new QualificationCommitments(["T02", "T03", "H02", "H01", "H03"], 1_000_000_000, 2_000_000_000, 1_000_000_000);
        Check(prefix.RemainingBytes == 17_643_108_864);
        foreach (var label in new[] { "T02", "T03", "H02" }) prefix.Seal(label);
        Check(prefix.RemainingBytes == 8_643_108_864);
        Check(77_640_138_752L >= 60_000_000_000L + prefix.RemainingBytes);
        Check(60_000_000_000L - 1 < 60_000_000_000L + prefix.RemainingBytes);
        var heldouts = new QualificationCommitments(["H01", "H03"], 1_000_000_000, 2_000_000_000, 1_000_000_000);
        Check(heldouts.RemainingBytes == 8_643_108_864);
        heldouts.Seal("H01"); Check(heldouts.RemainingBytes == 5_643_108_864);
        heldouts.Seal("H03"); Check(heldouts.RemainingBytes == 2_643_108_864);
        bool rejected = false; try { heldouts.Seal("T01"); } catch (IOException) { rejected = true; } Check(rejected);
        Console.WriteLine("Qualification commitments: conservative initial peak, sequential materialization, runtime exact-fit/shortfall, stage/closure, overflow, sealed, duplicate and continuation controls passed; no native or media action.");
        return 0;
    }
}
