namespace Z80.Core;

/// <summary>
/// The Z80 register file: main and shadow register sets, index registers, SP/PC,
/// I/R, the internal WZ (MEMPTR) register, interrupt flip-flops/mode, and Q (the
/// "did the last instruction modify flags" latch used for undocumented X/Y flag
/// behaviour across instruction pairs).
/// </summary>
public struct Registers
{
    public byte A, F;
    public byte B, C;
    public byte D, E;
    public byte H, L;

    public byte A_, F_;
    public byte B_, C_;
    public byte D_, E_;
    public byte H_, L_;

    public byte IXH, IXL;
    public byte IYH, IYL;

    public ushort SP;
    public ushort PC;

    public byte I;
    public byte R;

    /// <summary>Internal WZ / MEMPTR register.</summary>
    public ushort WZ;

    public bool IFF1;
    public bool IFF2;

    /// <summary>Interrupt mode: 0, 1, or 2.</summary>
    public byte IM;

    /// <summary>
    /// Tracks whether the most recently executed instruction modified the flag
    /// register, for the undocumented bit 3/5 (XF/YF) behaviour of BIT/SCF/CCF.
    /// </summary>
    public byte Q;

    /// <summary>
    /// True only immediately after an EI instruction: the one-instruction delay
    /// before interrupts actually become enabled. Cleared after any instruction
    /// (including the one EI delayed) unless that instruction is itself EI.
    /// </summary>
    public bool EiPending;

    /// <summary>True only immediately after LD A,I or LD A,R (affects the
    /// undocumented P/V flag behaviour of those instructions).</summary>
    public bool LastWasLdAIR;

    public ushort AF
    {
        readonly get => (ushort)((A << 8) | F);
        set { A = (byte)(value >> 8); F = (byte)value; }
    }

    public ushort BC
    {
        readonly get => (ushort)((B << 8) | C);
        set { B = (byte)(value >> 8); C = (byte)value; }
    }

    public ushort DE
    {
        readonly get => (ushort)((D << 8) | E);
        set { D = (byte)(value >> 8); E = (byte)value; }
    }

    public ushort HL
    {
        readonly get => (ushort)((H << 8) | L);
        set { H = (byte)(value >> 8); L = (byte)value; }
    }

    public ushort AF_
    {
        readonly get => (ushort)((A_ << 8) | F_);
        set { A_ = (byte)(value >> 8); F_ = (byte)value; }
    }

    public ushort BC_
    {
        readonly get => (ushort)((B_ << 8) | C_);
        set { B_ = (byte)(value >> 8); C_ = (byte)value; }
    }

    public ushort DE_
    {
        readonly get => (ushort)((D_ << 8) | E_);
        set { D_ = (byte)(value >> 8); E_ = (byte)value; }
    }

    public ushort HL_
    {
        readonly get => (ushort)((H_ << 8) | L_);
        set { H_ = (byte)(value >> 8); L_ = (byte)value; }
    }

    public ushort IX
    {
        readonly get => (ushort)((IXH << 8) | IXL);
        set { IXH = (byte)(value >> 8); IXL = (byte)value; }
    }

    public ushort IY
    {
        readonly get => (ushort)((IYH << 8) | IYL);
        set { IYH = (byte)(value >> 8); IYL = (byte)value; }
    }

    /// <summary>I:R refresh address, as driven onto the address bus during the
    /// T3/T4 ticks of an M1 opcode fetch.</summary>
    public readonly ushort RefreshAddress => (ushort)((I << 8) | R);

    /// <summary>Increments the low 7 bits of R, preserving bit 7, as happens on
    /// every M1 fetch (including each byte of a multi-byte prefix chain).</summary>
    public void BumpR() => R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
}
