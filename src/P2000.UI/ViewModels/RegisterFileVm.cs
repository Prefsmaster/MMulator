using CommunityToolkit.Mvvm.ComponentModel;
using P2000.Machine.Debug;

namespace P2000.UI.ViewModels;

/// <summary>
/// Displays the full Z80 register file from the most-recent <see cref="MachineSnapshot"/>.
/// Updated on break/step; properties read "–" when no snapshot is available (machine running).
/// Manual properties used throughout so two-letter acronyms (AF, BC…) appear as public names.
/// </summary>
public sealed class RegisterFileVm : ObservableObject
{
    private const string None = "–";

    private bool   _hasSnapshot;
    private string _af = None, _bc = None, _de = None, _hl = None;
    private string _af2 = None, _bc2 = None, _de2 = None, _hl2 = None;
    private string _ix = None, _iy = None, _sp = None, _pc = None;
    private string _i = None, _r = None, _wz = None;
    private string _f = None, _flagsText = None;
    private string _iff1 = None, _iff2 = None, _im = None;
    private string _fieldTState = None;

    public bool   HasSnapshot  { get => _hasSnapshot;  set => SetProperty(ref _hasSnapshot,  value); }

    public string AF           { get => _af;           set => SetProperty(ref _af,           value); }
    public string BC           { get => _bc;           set => SetProperty(ref _bc,           value); }
    public string DE           { get => _de;           set => SetProperty(ref _de,           value); }
    public string HL           { get => _hl;           set => SetProperty(ref _hl,           value); }

    public string AF2          { get => _af2;          set => SetProperty(ref _af2,          value); }
    public string BC2          { get => _bc2;          set => SetProperty(ref _bc2,          value); }
    public string DE2          { get => _de2;          set => SetProperty(ref _de2,          value); }
    public string HL2          { get => _hl2;          set => SetProperty(ref _hl2,          value); }

    public string IX           { get => _ix;           set => SetProperty(ref _ix,           value); }
    public string IY           { get => _iy;           set => SetProperty(ref _iy,           value); }
    public string SP           { get => _sp;           set => SetProperty(ref _sp,           value); }
    public string PC           { get => _pc;           set => SetProperty(ref _pc,           value); }

    public string I            { get => _i;            set => SetProperty(ref _i,            value); }
    public string R            { get => _r;            set => SetProperty(ref _r,            value); }
    public string WZ           { get => _wz;           set => SetProperty(ref _wz,           value); }

    public string F            { get => _f;            set => SetProperty(ref _f,            value); }
    public string FlagsText    { get => _flagsText;    set => SetProperty(ref _flagsText,    value); }

    public string IFF1         { get => _iff1;         set => SetProperty(ref _iff1,         value); }
    public string IFF2         { get => _iff2;         set => SetProperty(ref _iff2,         value); }
    public string IM           { get => _im;           set => SetProperty(ref _im,           value); }

    public string FieldTState  { get => _fieldTState;  set => SetProperty(ref _fieldTState,  value); }

    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Populate from live CPU register state (best-effort; call on UI thread).
    /// Used while the machine is running — values may be mid-instruction.
    /// </summary>
    public void UpdateLive(in Z80.Core.Registers reg, int fieldTState)
    {
        HasSnapshot = true;

        AF  = $"{reg.AF:X4}";
        BC  = $"{reg.BC:X4}";
        DE  = $"{reg.DE:X4}";
        HL  = $"{reg.HL:X4}";

        AF2 = $"{reg.AF_:X4}";
        BC2 = $"{reg.BC_:X4}";
        DE2 = $"{reg.DE_:X4}";
        HL2 = $"{reg.HL_:X4}";

        IX  = $"{reg.IX:X4}";
        IY  = $"{reg.IY:X4}";
        SP  = $"{reg.SP:X4}";
        PC  = $"{reg.PC:X4}";

        I   = $"{reg.I:X2}";
        R   = $"{reg.R:X2}";
        WZ  = $"{reg.WZ:X4}";

        F          = $"{reg.F:X2}";
        FlagsText  = BuildFlagsFromByte(reg.F);

        IFF1 = reg.IFF1 ? "1" : "0";
        IFF2 = reg.IFF2 ? "1" : "0";
        IM   = reg.IM.ToString();

        FieldTState = fieldTState.ToString();
    }

    /// <summary>Populate all properties from <paramref name="snap"/> (call on UI thread).</summary>
    public void Update(MachineSnapshot snap)
    {
        HasSnapshot = true;

        AF  = $"{snap.AF:X4}";
        BC  = $"{snap.BC:X4}";
        DE  = $"{snap.DE:X4}";
        HL  = $"{snap.HL:X4}";

        AF2 = $"{snap.AF_:X4}";
        BC2 = $"{snap.BC_:X4}";
        DE2 = $"{snap.DE_:X4}";
        HL2 = $"{snap.HL_:X4}";

        IX  = $"{snap.IX:X4}";
        IY  = $"{snap.IY:X4}";
        SP  = $"{snap.SP:X4}";
        PC  = $"{snap.PC:X4}";

        I   = $"{snap.I:X2}";
        R   = $"{snap.R:X2}";
        WZ  = $"{snap.WZ:X4}";

        F          = $"{snap.F:X2}";
        FlagsText  = BuildFlags(snap);

        IFF1 = snap.IFF1 ? "1" : "0";
        IFF2 = snap.IFF2 ? "1" : "0";
        IM   = snap.IM.ToString();

        FieldTState = snap.FieldTState.ToString();
    }

    /// <summary>Clear all properties (machine resumed after break).</summary>
    public void Clear()
    {
        HasSnapshot = false;
        AF = BC = DE = HL = None;
        AF2 = BC2 = DE2 = HL2 = None;
        IX = IY = SP = PC = None;
        I = R = WZ = F = None;
        FlagsText = None;
        IFF1 = IFF2 = IM = None;
        FieldTState = None;
    }

    private static string BuildFlags(MachineSnapshot s)
        => BuildFlagsFromByte(s.F);

    private static string BuildFlagsFromByte(byte f)
        => $"{((f & 0x80) != 0 ? 'S' : 's')}{((f & 0x40) != 0 ? 'Z' : 'z')}" +
           $"{((f & 0x20) != 0 ? 'Y' : 'y')}{((f & 0x10) != 0 ? 'H' : 'h')}" +
           $"{((f & 0x08) != 0 ? 'X' : 'x')}{((f & 0x04) != 0 ? 'P' : 'p')}" +
           $"{((f & 0x02) != 0 ? 'N' : 'n')}{((f & 0x01) != 0 ? 'C' : 'c')}";
}
