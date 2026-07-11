namespace P2000.Machine.Devices.Cassette;

/// <summary>
/// Selects the MDCR timing model (MDCR-implementation.md §0).
/// <list type="bullet">
/// <item><b>Authentic (default):</b> phase-by-phase bitstream at 209 cycles/phase — cycle-exact,
/// the ROM's own bit-recovery loop drives clock and data. Deterministic; point all regression
/// tests here.</item>
/// <item><b>Turbo:</b> ROM-trap block transfer — instant load/save, bitstream bypassed.
/// Traps <c>cas_Read</c> (0x0552) and <c>cas_Write</c> (0x057A) — see
/// <see cref="P2000.Machine.Devices.Cassette.CassetteTurboTrap"/> (project CLAUDE.md
/// §13.18) for the full calling-convention rationale.</item>
/// </list>
/// </summary>
public enum TimingPolicy
{
    Authentic,
    Turbo,
}
