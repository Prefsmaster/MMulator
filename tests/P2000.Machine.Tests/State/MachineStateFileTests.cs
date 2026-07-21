using P2000.Machine.Contention;
using P2000.Machine.Memory;
using P2000.Machine.State;

namespace P2000.Machine.Tests.State;

/// <summary>
/// Round-trip, determinism, and version-gate tests for <see cref="MachineStateFile"/>
/// (.state binary format, project CLAUDE.md §11 validation gate §12.5).
/// </summary>
public class MachineStateFileTests
{
    // ---- Round-trip helpers -----------------------------------------------------------------

    /// <summary>Runs <paramref name="machine"/> for one full field (50 000 T-states), which
    /// guarantees a <see cref="Video.FieldComplete"/> boundary where <c>AtInstructionBoundary</c>
    /// holds for the real monitor ROM (it polls CIP in a tight loop, always landing on a
    /// boundary at the field tick).</summary>
    private static void RunOneField(Machine machine)
    {
        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
            machine.Tick();
    }

    /// <summary>Advances <paramref name="machine"/> to the next instruction boundary (at most
    /// a handful of ticks in the monitor ROM's tight polling loops) then saves and reloads.</summary>
    private static Machine SaveAndReload(Machine machine)
    {
        while (!machine.Cpu.AtInstructionBoundary)
            machine.Tick();
        var ms = new MemoryStream();
        MachineStateFile.Save(machine, ms);
        ms.Position = 0;
        return MachineStateFile.Load(ms);
    }

    // ---- Determinism (the headline gate §12.5) ----------------------------------------------

    /// <summary>
    /// After save + reload, running both machines for the same number of ticks must produce
    /// identical CPU register state and VRAM contents.
    ///
    /// Uses the REAL embedded monitor ROM (not a synthetic one) because ROM is intentionally
    /// NOT included in the .state file — only DRAM (VRAM + RAM) is saved. A synthetic ROM
    /// overwritten via <c>LoadRom</c> would be lost on restore, causing divergence.
    ///
    /// The monitor ROM enters a tight CIP-polling loop after ~1 field; from that point both
    /// machines execute identical deterministic code from identical saved state.
    /// </summary>
    [Fact]
    public void StateRoundTrip_SubsequentExecution_ProducesIdenticalCpuAndMemoryState()
    {
        var original = new Machine();
        // Write a recognisable pattern to VRAM so we can verify memory state survives round-trip.
        for (var i = 0; i < 40; i++)
            original.Memory.Write((ushort)(PageTable.VideoRamStart + i), (byte)(i + 1));

        // Run one full field — the monitor ROM completes its boot sequence and enters its
        // deterministic CIP-polling loop, where it stays (no cassette is mounted).
        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++)
            original.Tick();

        // Save at instruction boundary, reload.
        var restored = SaveAndReload(original);

        // Run both machines for exactly one more field and compare authoritative state.
        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++) original.Tick();
        for (var i = 0; i < VideoFetchUnit.TStatesPerField; i++) restored.Tick();

        Assert.Equal(original.Cpu.Reg.PC, restored.Cpu.Reg.PC);
        Assert.Equal(original.Cpu.Reg.SP, restored.Cpu.Reg.SP);
        // VRAM must also match (proves memory state is identical).
        for (var addr = PageTable.VideoRamStart; addr < PageTable.VideoRamStart + 40; addr++)
            Assert.Equal(original.Memory.Read((ushort)addr), restored.Memory.Read((ushort)addr));
    }

    [Fact]
    public void StateRoundTrip_CpuRegisters_ArePreserved()
    {
        var machine = new Machine();
        // Run a program that modifies registers visibly.
        machine.Memory.LoadRom(new byte[]
        {
            0x3E, 0x42, // LD A, 0x42
            0x06, 0x07, // LD B, 0x07
            0x18, 0xFE, // JR -2  (infinite spin; HALT cannot be used — AtInstructionBoundary
                        //         is false while halted, so SaveAndReload would loop forever)
        });
        for (var i = 0; i < 40; i++) machine.Tick();

        var restored = SaveAndReload(machine);

        Assert.Equal(machine.Cpu.Reg.A, restored.Cpu.Reg.A);
        Assert.Equal(machine.Cpu.Reg.B, restored.Cpu.Reg.B);
        Assert.Equal(machine.Cpu.Reg.PC, restored.Cpu.Reg.PC);
        Assert.Equal(machine.Cpu.Reg.SP, restored.Cpu.Reg.SP);
        Assert.Equal(machine.Cpu.Reg.IFF1, restored.Cpu.Reg.IFF1);
        Assert.Equal(machine.Cpu.Reg.IM, restored.Cpu.Reg.IM);
    }

    [Fact]
    public void StateRoundTrip_RamContents_ArePreserved()
    {
        var machine = new Machine();
        // Write a distinctive pattern to base RAM.
        machine.Memory.Write(PageTable.BaseRamStart, 0xDE);
        machine.Memory.Write(PageTable.BaseRamStart + 1, 0xAD);

        var restored = SaveAndReload(machine);

        Assert.Equal(0xDE, restored.Memory.Read(PageTable.BaseRamStart));
        Assert.Equal(0xAD, restored.Memory.Read(PageTable.BaseRamStart + 1));
    }

    [Fact]
    public void StateRoundTrip_VideoRam_IsPreserved()
    {
        var machine = new Machine();
        machine.Memory.Write(PageTable.VideoRamStart, (byte)'A');
        machine.Memory.Write(PageTable.VideoRamStart + 1, (byte)'B');

        var restored = SaveAndReload(machine);

        Assert.Equal((byte)'A', restored.Memory.Read(PageTable.VideoRamStart));
        Assert.Equal((byte)'B', restored.Memory.Read(PageTable.VideoRamStart + 1));
    }

    [Fact]
    public void StateRoundTrip_BankedRam_IsPreserved()
    {
        var machine = new Machine(new MachineConfig { RamVariant = RamVariant.T102 });
        machine.Memory.SelectBank(2);
        machine.Memory.Write(PageTable.BankedWindowStart, 0xBE);
        machine.Memory.SelectBank(0);

        var restored = SaveAndReload(machine);

        restored.Memory.SelectBank(2);
        Assert.Equal(0xBE, restored.Memory.Read(PageTable.BankedWindowStart));
    }

    [Fact]
    public void StateRoundTrip_EmbeddedConfigIsPreserved()
    {
        var config = new MachineConfig { RamVariant = RamVariant.T54, Board = InternalBoard.RamOnly };
        var machine = new Machine(config);

        var restored = SaveAndReload(machine);

        Assert.Equal(config.RamVariant, restored.Config.RamVariant);
        Assert.Equal(config.Board, restored.Config.Board);
    }

    /// <summary>Project CLAUDE.md §13 milestone 17: the optional Ctc block (present only when
    /// the FloppyRam board is fitted) and the aggregator's Lock bool both survive a round trip.</summary>
    [Fact]
    public void StateRoundTrip_CtcAndLock_ArePreserved()
    {
        var machine = new Machine(new MachineConfig { Board = InternalBoard.FloppyRam, RamVariant = RamVariant.T102 });
        machine.Ports.Write(0x88, 0x20);       // CTC vector base
        machine.Ports.Write(0x8B, 0xD5);       // ch3: counter mode, INTEN, TCNEXT, rising trig
        machine.Ports.Write(0x8B, 0x01);       // TC = 1

        var restored = SaveAndReload(machine);

        Assert.True(restored.Interrupts.LockAsserted);
        Assert.NotNull(restored.Ctc);

        // Vector base + programming survived: one CLK/TRG edge fires ch3 with vector 0x26.
        restored.Ctc!.ClkTrg(3);
        Assert.Equal(0x26, restored.Ctc.DaisyChainDevices[3].Acknowledge());
    }

    // ---- Stream/file API -------------------------------------------------------------------

    [Fact]
    public void Save_And_Load_ViaStream_Succeeds()
    {
        var machine = new Machine();
        var ms = new MemoryStream();
        MachineStateFile.Save(machine, ms);
        ms.Position = 0;
        var restored = MachineStateFile.Load(ms);
        Assert.NotNull(restored);
    }

    // ---- Bad magic / version ---------------------------------------------------------------

    [Fact]
    public void Load_BadMagic_Throws()
    {
        var ms = new MemoryStream();
        ms.Write("NOPE"u8);
        ms.Write(new byte[20]);
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => MachineStateFile.Load(ms));
    }

    [Fact]
    public void Load_FutureVersion_Throws()
    {
        var ms = new MemoryStream();
        ms.Write("P2ST"u8);
        var futureVersion = MachineStateFile.CurrentVersion + 1;
        ms.Write(new byte[] {
            (byte)futureVersion, 0, 0, 0,
            0, 0, 0, 0, // config length = 0
        });
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => MachineStateFile.Load(ms));
    }

    [Fact]
    public void Load_VersionZero_Throws()
    {
        var ms = new MemoryStream();
        ms.Write("P2ST"u8);
        ms.Write(new byte[] { 0, 0, 0, 0 }); // version = 0
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => MachineStateFile.Load(ms));
    }

    [Fact]
    public void Load_VersionOne_Throws()
    {
        // v1 files are incompatible: SoundDevice block missing + Interrupts wrote only 1 bool.
        // They were produced between milestones 11 and 16; reject cleanly rather than misloading.
        var ms = new MemoryStream();
        ms.Write("P2ST"u8);
        ms.Write(new byte[] { 1, 0, 0, 0 }); // version = 1
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => MachineStateFile.Load(ms));
    }

    [Fact]
    public void Load_VersionTwo_Throws()
    {
        // v2 files are incompatible: Interrupts wrote only 2 bools (no Lock) and no Ctc block.
        // Produced between milestones 16 and 17; reject cleanly rather than misloading.
        var ms = new MemoryStream();
        ms.Write("P2ST"u8);
        ms.Write(new byte[] { 2, 0, 0, 0 }); // version = 2
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => MachineStateFile.Load(ms));
    }
}
