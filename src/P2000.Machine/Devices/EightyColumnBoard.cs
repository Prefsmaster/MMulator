using P2000.Machine.State;

namespace P2000.Machine.Devices;

/// <summary>
/// The 80-character daughterboard (P2000 Nieuwsbrief §13.25, February 1986 — translated in
/// <c>docs/P2000T-80column-board-1986-newsletter.md</c>; reference doc §5 "80-column mode").
/// An opt-in, T-only *modification*, not a slot card: the SAA5020 is desoldered from the CPU
/// board, re-seated on a daughterboard mounted above its vacated socket, and nine flying leads
/// tap five motherboard ICs.
///
/// <b>All this object models is the mode latch and its read-back</b>, because that is the whole
/// of the board's CPU-visible surface (§13.25.2: the <c>OUT</c> decode is a half 74S74 ON THE
/// BOARD, and the read-back path is the gate group drawn above the half 74LS74, also on the
/// board — nothing on an unmodified T latches port 0x00 bit 0 or answers port 0x70). The
/// cadence doubling itself is a parameter on the existing SAA5020 fetch-timing unit
/// (<see cref="Contention.VideoFetchUnit.EightyColumn"/>), reached via
/// <see cref="Video.SetEightyColumnMode"/> — deliberately NOT a second video path.
///
/// <b>Presence probing is load-bearing</b> (§13.25.9): "by switching back and forth a few times
/// between 80 and 40 characters and each time checking whether this has been taken over, a
/// program can 'see' whether an 80-character board is present." That only works if, with no
/// board fitted, port 0x70 is genuinely UNCLAIMED and reads open bus (0xFF) — a zero-returning
/// stub would make every probing program conclude "board present, currently 40-column". Hence
/// this device is instantiated and registered ONLY when the board is fitted; there is no
/// always-present stub.
///
/// Not modelled, deliberately: the <c>80 kar</c> hardware forcing contact (§13.25.9). It has no
/// port, no software drives it, and the article explicitly advises against using it.
/// </summary>
public sealed class EightyColumnBoard : IDevice
{
    /// <summary>Write port for the mode latch — <c>OUT 0,1</c> gives 80 characters,
    /// <c>OUT 0,0</c> gives 40 (§13.25.9). Only bit 0 is latched; the other seven are ignored.
    /// Reads of 0x00-0x09 remain the keyboard's (reference doc §5f) — this device claims the
    /// WRITE side of port 0x00 only, which nothing else on the T uses.</summary>
    public const byte ModePort = 0x00;

    /// <summary>Read port for the latch read-back — <c>A = INP(&amp;H70)</c> gives 0 at 40
    /// characters, 1 at 80 (§13.25.9).</summary>
    public const byte StatusPort = 0x70;

    private bool _eightyColumn;

    /// <summary>Current mode: <c>false</c> = 40 columns, <c>true</c> = 80.</summary>
    public bool EightyColumn => _eightyColumn;

    /// <summary>Raised whenever the latch changes value, so the video device can re-parameterise
    /// the fetch cadence. Fires only on an actual change — a redundant <c>OUT 0,0</c> in
    /// 40-column mode is a no-op, matching a real flip-flop being re-clocked to the value it
    /// already holds.</summary>
    public event Action<bool>? ModeChanged;

    /// <summary>Port 0x00 write: latch bit 0, ignore bits 1-7. Effective immediately, mid-frame
    /// included — the LS157 multiplexer switches the instant the S74 does; there is no
    /// synchronisation to a frame or line boundary on real hardware, so do not defer it.</summary>
    public void WriteModePort(byte value) => SetMode((value & 0x01) != 0);

    /// <summary>Port 0x70 read: <c>0x01</c> in 80-column mode, <c>0x00</c> in 40-column mode.
    ///
    /// The article states the whole returned byte as 0 or 1, so the board appears to drive all
    /// eight bits with the upper seven low — <b>likely, but not certain</b>: BASIC's <c>INP</c>
    /// shorthand could be hiding a mask, and the schematic transcription doesn't settle how
    /// many bits the read-back buffer drives. Upper bits are implemented as 0; revisit only
    /// against a real board.</summary>
    public byte ReadStatusPort() => _eightyColumn ? (byte)0x01 : (byte)0x00;

    /// <summary>40 columns on BOTH cold and warm reset — "Bij RESET wordt automatisch de 40
    /// karakter-stand gekozen" (§13.25.9). Note this differs from the RAM rule (§5b), where a
    /// warm reset deliberately preserves contents: the mode latch is a flip-flop on the board's
    /// reset line, not memory.</summary>
    public void Reset() => SetMode(false);

    private void SetMode(bool eightyColumn)
    {
        if (_eightyColumn == eightyColumn) return;
        _eightyColumn = eightyColumn;
        ModeChanged?.Invoke(eightyColumn);
    }

    public void SaveState(IStateWriter writer) => writer.WriteBool(_eightyColumn);

    /// <summary>Restores the mode bit. Does NOT raise <see cref="ModeChanged"/> —
    /// <see cref="Machine"/> re-applies the restored mode to the video device explicitly after
    /// the whole device walk, so the scroll register restored from the same state file isn't
    /// clobbered by the mode latch's hardware-clear side effect.</summary>
    public void LoadState(IStateReader reader) => _eightyColumn = reader.ReadBool();
}
