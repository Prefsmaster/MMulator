namespace P2000.Machine.Devices.Cassette;

/// <summary>
/// Selects the MDCR timing model (MDCR-implementation.md §0).
/// <list type="bullet">
/// <item><b>Authentic (default):</b> phase-by-phase bitstream at 209 cycles/phase — cycle-exact,
/// the ROM's own bit-recovery loop drives clock and data. Deterministic; point all regression
/// tests here.</item>
/// <item><b>Turbo:</b> ROM-trap block transfer — instant load/save, bitstream bypassed. Trap
/// addresses (<c>cas_Write</c> / <c>write_block</c>) are deferred pending ROM disassembly
/// confirmation (MDCR-implementation.md §5). Infrastructure is wired; actual traps TBD.</item>
/// </list>
/// </summary>
public enum TimingPolicy
{
    Authentic,
    Turbo,
}
