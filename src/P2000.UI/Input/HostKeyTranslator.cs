using Avalonia.Input;

namespace P2000.UI.Input;

/// <summary>
/// Translates host key down/up events into P2000 matrix (row, col, pressed) events, honoring
/// the active <see cref="KeyMappingMode"/> (project CLAUDE.md §14.3a). Shared by the display
/// window's host-key handler and the soft-keyboard window's shifted-click behaviour, so both
/// obey the same mode.
///
/// P2000-Authentic mode is pure passthrough (host Shift key presses the P2000 Shift matrix
/// position directly; every other key presses whatever position <see cref="KeyMap.Map"/>
/// returns) — the P2000's own shift-symbol pairing falls out for free.
///
/// Standard-Host mode may need to press a P2000 key that isn't at the host key's position, and
/// may need to force P2000 Shift to a state that doesn't match whether the host's own Shift key
/// is physically down (project CLAUDE.md §14.3a, e.g. host Shift+2 → '@' needs the P2000 key
/// UNshifted even though the host Shift is held). This class remembers exactly what it pressed
/// per host key so KeyUp releases the same thing regardless of any Shift change in between —
/// and refcounts forced-shift keys so simple rollover (typing quickly) does not toggle P2000
/// Shift for a key that is not actually asking for a change.
///
/// "Forcing Shift off" must release whichever REAL shift crosspoint(s) are actually down — the
/// P2000 has two, (9,0) for Left Shift and (9,7) for Right Shift, and pressing either one is
/// enough for the ROM to see Shift asserted. An earlier version hardcoded (9,0), so releasing
/// it while the user was actually holding Right Shift left (9,7) asserted the whole time —
/// pressing '@' still read as Shift-held and produced the umlaut/diaeresis key instead
/// (owner-reported 2026-07-20). Track the real shift keys explicitly instead of assuming one.
///
/// <b>Force-OFF needs a real field-boundary gap before the target key press (owner-reported
/// 2026-07-20, confirmed by a machine-level diagnostic):</b> releasing the real Shift
/// crosspoint(s) and pressing the target key in the exact same synchronous instant still reads
/// as shifted on the real ROM — Shift+2 produced `^` (the same result as genuinely holding
/// Shift), not `@`. A one-field (20 ms) gap between the release and the press fixed it in
/// every case tested; the P2000's keyboard scan apparently needs to observe a moment with
/// Shift genuinely released before it will register a subsequent keypress as unshifted.
/// The force-ON case (no real Shift held at all, e.g. plain `=` or `[`) does NOT need this —
/// confirmed working with zero gap — because there's no stale "already pressed" state to
/// escape; asserting Shift and the target key together is exactly how a normal Shift+key combo
/// already works. Only <see cref="ForceOffGapAsync"/> below carries the delay.
/// </summary>
public sealed class HostKeyTranslator
{
    public KeyMappingMode Mode { get; set; } = KeyMappingMode.P2000Authentic;

    /// <summary>Raised for each matrix event this translator produces: (row, col, pressed).</summary>
    public event Action<int, int, bool>? MatrixEvent;

    private readonly HashSet<Key> _keysDown = new();          // OS auto-repeat suppression
    private readonly Dictionary<Key, (int Row, int Col)> _activePress = new();
    private readonly Dictionary<Key, bool> _activeForce = new(); // true=forced Shift ON, false=forced OFF
    private readonly HashSet<Key> _realShiftDown = new();     // whichever of LeftShift/RightShift are actually down
    private bool _hostShiftDown;
    private int _forceOnCount;
    private int _forceOffCount;

    // Used only when forcing Shift ON with no real host Shift down at all (so nothing to conflict with).
    private const int SyntheticShiftRow = 9, SyntheticShiftCol = 0;

    // Gap the ROM needs to observe a genuinely-released Shift before trusting a subsequent
    // keypress as unshifted (see the class doc above) — one 20 ms field plus safety margin.
    private const int ForceOffGapMilliseconds = 40;

    // Windows (with NumLock ON) reports the NAVIGATION key, not the digit, when Shift is held
    // while a numpad key is pressed — e.g. Shift+NumPad1 delivers Key.End, not Key.NumPad1
    // (owner-reported 2026-07-19: Shift+numpad-1 didn't activate ZOEK). This is indistinguishable
    // from a real press of the dedicated End/Home/Arrow keys by Key alone, but Avalonia's
    // PhysicalKey is scancode-based and unaffected by the OS's Shift+NumLock override — so the
    // numpad's true identity is recovered from PhysicalKey before anything else runs.
    private static readonly Dictionary<PhysicalKey, Key> _physicalNumpadOverride = new()
    {
        { PhysicalKey.NumPad0, Key.NumPad0 },
        { PhysicalKey.NumPad1, Key.NumPad1 },
        { PhysicalKey.NumPad2, Key.NumPad2 },
        { PhysicalKey.NumPad3, Key.NumPad3 },
        { PhysicalKey.NumPad4, Key.NumPad4 },
        { PhysicalKey.NumPad5, Key.NumPad5 },
        { PhysicalKey.NumPad6, Key.NumPad6 },
        { PhysicalKey.NumPad7, Key.NumPad7 },
        { PhysicalKey.NumPad8, Key.NumPad8 },
        { PhysicalKey.NumPad9, Key.NumPad9 },
        { PhysicalKey.NumPadDecimal, Key.Decimal },
    };

    private static Key Normalize(Key key, PhysicalKey physicalKey)
        => _physicalNumpadOverride.TryGetValue(physicalKey, out var canonical) ? canonical : key;

    /// <summary>Presses the P2000 key(s) corresponding to <paramref name="key"/>, if any.
    /// Returns whether the key is part of the P2000 matrix at all (in the current mode) — the
    /// caller (e.g. the display window) uses this to decide whether to mark the host event
    /// handled, so unrecognized keys (F5, F11, …) still reach the window's own KeyBindings.
    /// <paramref name="physicalKey"/> (optional — omitted by the soft-keyboard's synthetic
    /// presses, which have no ambiguity to resolve) recovers the true numpad key identity when
    /// Windows' Shift+NumLock override has swapped it for a navigation key — see the class doc.</summary>
    public bool KeyDown(Key key, PhysicalKey physicalKey = default)
    {
        key = Normalize(key, physicalKey);
        if (!_keysDown.Add(key)) return IsRecognized(key); // OS repeat — P2000T's 50 Hz ISR handles repeat itself

        if (key is Key.LeftShift or Key.RightShift)
        {
            _hostShiftDown = true;
            _realShiftDown.Add(key);
            var pos = key == Key.RightShift ? (9, 7) : (9, 0);
            _activePress[key] = pos;
            if (_forceOffCount == 0) Emit(pos, true);
            return true;
        }

        (int Row, int Col) target;
        bool? needsShift;

        if (Mode == KeyMappingMode.P2000Authentic)
        {
            var m = KeyMap.Map(key);
            if (m is null) return false;
            target = (m.Value.Row, m.Value.Col);
            needsShift = null; // passthrough — P2000 shift key (handled above) already carries the state
        }
        else
        {
            var t = KeyMap.MapStandardHost(key, _hostShiftDown);
            if (t is null) return false; // no P2000 equivalent — silently ignored (owner-decided 2026-07-19)
            target = (t.Value.Row, t.Value.Col);
            needsShift = t.Value.NeedsShift;
        }

        _activePress[key] = target; // recorded up front so KeyUp knows what to release even if
                                     // the press below is still pending behind the force-off gap.

        if (needsShift is bool wants && wants != _hostShiftDown)
        {
            _activeForce[key] = wants;
            if (wants)
            {
                if (_forceOnCount++ == 0 && !_hostShiftDown) Emit((SyntheticShiftRow, SyntheticShiftCol), true);
                Emit(target, true); // force-ON needs no gap — confirmed by diagnostic
            }
            else
            {
                if (_forceOffCount++ == 0 && _hostShiftDown)
                {
                    ReleaseRealShifts();
                    _ = PressAfterForceOffGapAsync(target); // needs the gap — see class doc
                }
                else
                {
                    Emit(target, true); // Shift already suppressed by another concurrent key
                }
            }
        }
        else
        {
            Emit(target, true);
        }
        return true;
    }

    private async Task PressAfterForceOffGapAsync((int Row, int Col) target)
    {
        await Task.Delay(ForceOffGapMilliseconds);
        Emit(target, true);
    }

    /// <summary>Releases whatever <see cref="KeyDown"/> pressed for this key. Return value has
    /// the same meaning as <see cref="KeyDown"/>'s. <paramref name="physicalKey"/> must match
    /// whatever was passed to the corresponding <see cref="KeyDown"/> call — see its doc.</summary>
    public bool KeyUp(Key key, PhysicalKey physicalKey = default)
    {
        key = Normalize(key, physicalKey);
        _keysDown.Remove(key);
        if (!_activePress.Remove(key, out var pos)) return IsRecognized(key);

        if (key is Key.LeftShift or Key.RightShift)
        {
            _realShiftDown.Remove(key);
            _hostShiftDown = _realShiftDown.Count > 0;
            if (_forceOffCount == 0) Emit(pos, false);
            return true;
        }

        Emit(pos, false);

        if (_activeForce.Remove(key, out var wasForcedOn))
        {
            if (wasForcedOn)
            {
                if (--_forceOnCount == 0 && !_hostShiftDown) Emit((SyntheticShiftRow, SyntheticShiftCol), false);
            }
            else
            {
                if (--_forceOffCount == 0 && _hostShiftDown) RestoreRealShifts();
            }
        }
        return true;
    }

    // Releases/restores exactly the real Shift crosspoint(s) actually held (could in principle
    // be both LeftShift and RightShift at once) — never the hardcoded (9,0) a prior version used,
    // which silently no-op'd when the user was holding RightShift instead (owner-reported bug).
    private void ReleaseRealShifts()
    {
        foreach (var k in _realShiftDown) Emit(_activePress[k], false);
    }

    private void RestoreRealShifts()
    {
        foreach (var k in _realShiftDown) Emit(_activePress[k], true);
    }

    private bool IsRecognized(Key key)
    {
        if (key is Key.LeftShift or Key.RightShift) return true;
        return Mode == KeyMappingMode.P2000Authentic
            ? KeyMap.Map(key) is not null
            : KeyMap.MapStandardHost(key, _hostShiftDown) is not null;
    }

    private void Emit((int Row, int Col) pos, bool pressed) => MatrixEvent?.Invoke(pos.Row, pos.Col, pressed);

    /// <summary>Directly enqueue a raw matrix event, bypassing key-identity bookkeeping. For
    /// soft-keyboard positions with no host-key equivalent at all (np00/TB, the envelope key,
    /// the "#/°" key) — those have no Standard-Host translation concept since no host keycap
    /// corresponds to them, so mode is irrelevant.</summary>
    public void PressRaw(int row, int col, bool pressed) => Emit((row, col), pressed);
}
