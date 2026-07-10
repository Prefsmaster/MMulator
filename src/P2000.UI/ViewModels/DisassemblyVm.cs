using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Z80.Disassembler;

namespace P2000.UI.ViewModels;

/// <summary>
/// Disassembly panel VM. Decodes instructions around PC and exposes them as a flat
/// observable list for the ListView in the debugger right panel.
/// </summary>
public sealed partial class DisassemblyVm : ObservableObject
{
    private readonly Disassembler _disasm = new();
    private ushort _lastPc = 0xFFFF; // sentinel — forces first refresh

    public ObservableCollection<DisassemblyLineVm> Lines { get; } = new();

    /// <summary>Lines decoded before PC.</summary>
    private const int LinesBefore = 8;
    /// <summary>Lines decoded after PC (including the PC line itself).</summary>
    private const int LinesAfter  = 16;

    /// <summary>Addresses that have exec breakpoints set (maintained by DebuggerWindowVm).</summary>
    public HashSet<ushort> BreakpointAddresses { get; } = new();

    /// <summary>
    /// Refresh the listing around <paramref name="pc"/> using <paramref name="readByte"/>.
    /// Called on break/step (exact snapshot) and on each frame when PC moves (live).
    /// </summary>
    public void Refresh(ushort pc, Func<ushort, byte> readByte)
    {
        _lastPc = pc;
        var decoded = SyncToPc.DecodeAround(_disasm, pc, readByte, LinesBefore, LinesAfter);

        // Rebuild the observable list in-place so the ListView doesn't flicker.
        int i = 0;
        foreach (var line in decoded)
        {
            string addrStr = $"{line.Address:X4}";
            string bytesStr = string.Join(' ', line.Bytes.Select(b => $"{b:X2}"));
            bool isPC = line.Address == pc;
            bool hasBp = BreakpointAddresses.Contains(line.Address);

            if (i < Lines.Count)
            {
                var vm = Lines[i];
                vm.Address      = addrStr;
                vm.Bytes        = bytesStr;
                vm.Text         = line.Text;
                vm.RawAddress   = line.Address;
                vm.IsPC         = isPC;
                vm.HasBreakpoint = hasBp;
            }
            else
            {
                Lines.Add(new DisassemblyLineVm
                {
                    Address      = addrStr,
                    Bytes        = bytesStr,
                    Text         = line.Text,
                    RawAddress   = line.Address,
                    IsPC         = isPC,
                    HasBreakpoint = hasBp,
                });
            }
            i++;
        }
        while (Lines.Count > i)
            Lines.RemoveAt(Lines.Count - 1);
    }

    /// <summary>Refresh breakpoint dots only (no PC move — avoids full re-decode).</summary>
    public void RefreshBreakpointDots()
    {
        foreach (var vm in Lines)
            vm.HasBreakpoint = BreakpointAddresses.Contains(vm.RawAddress);
    }

    /// <summary>
    /// Returns true when PC has moved enough to warrant a re-decode (avoids re-decoding
    /// on every frame when the machine is running but PC hasn't changed).
    /// </summary>
    public bool NeedsRefresh(ushort pc) => pc != _lastPc;
}
