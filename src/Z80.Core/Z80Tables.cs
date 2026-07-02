namespace Z80.Core;

/// <summary>
/// The Z80 operand orderings (condition codes, register pairs, 8-bit registers, ALU
/// ops, rotates, interrupt modes, block ops), indexed exactly the way the core's
/// bit-field decode (x/y/z/p/q, see CLAUDE.md root §3) resolves them. This is the
/// single source of truth both the core's behaviour (where it references these
/// tables) and <c>Z80.Disassembler</c>'s text rendering read from — the two must
/// never diverge on index-&gt;meaning.
///
/// Orderings were extracted from, and verified against, the core's actual decode
/// logic (<see cref="Z80"/>'s <c>Get8</c>/<c>Set8</c>, <c>Get16</c>/<c>Set16</c>,
/// <c>Get16Af</c>/<c>Set16Af</c>, <c>TestCondition</c>, <c>ApplyAluOp</c>,
/// <c>ApplyCbRotate</c> in Opcodes.cs/QuadrantCB.cs, and the ED block-instruction
/// dispatch in QuadrantED.cs) — not invented independently.
/// </summary>
public static class Z80Tables
{
    /// <summary>3-bit register code r[z]/r[y]: 0=B 1=C 2=D 3=E 4=H 5=L 6=(HL) 7=A.
    /// Matches <c>Z80.Get8</c>/<c>Set8</c>. Under DD/FD, index 4/5 substitute to
    /// IXH/IXL and index 6 substitutes to (IX+d)/(IY+d) — see CLAUDE_disassembler
    /// §9's mixed-operand quirk for when that substitution does NOT apply.</summary>
    public static readonly string[] R = { "B", "C", "D", "E", "H", "L", "(HL)", "A" };

    /// <summary>2-bit register-pair code rp[p]: 0=BC 1=DE 2=HL 3=SP. Matches
    /// <c>Z80.Get16</c>/<c>Set16</c>. Under DD/FD the HL slot (p=2) becomes IX/IY.</summary>
    public static readonly string[] Rp = { "BC", "DE", "HL", "SP" };

    /// <summary>2-bit register-pair code rp2[p], the PUSH/POP/AF variant: 0=BC 1=DE
    /// 2=HL 3=AF. Matches <c>Z80.Get16Af</c>/<c>Set16Af</c>.</summary>
    public static readonly string[] Rp2 = { "BC", "DE", "HL", "AF" };

    /// <summary>3-bit condition code cc[y]: 0=NZ 1=Z 2=NC 3=C 4=PO 5=PE 6=P 7=M.
    /// Matches <c>Z80.TestCondition</c>.</summary>
    public static readonly string[] Cc = { "NZ", "Z", "NC", "C", "PO", "PE", "P", "M" };

    /// <summary>3-bit ALU op alu[y], as a mnemonic prefix ready to prepend the
    /// operand: 0=ADD A, 1=ADC A, 2=SUB 3=SBC A, 4=AND 5=XOR 6=OR 7=CP. Matches
    /// <c>Z80.ApplyAluOp</c>.</summary>
    public static readonly string[] Alu =
    {
        "ADD A,", "ADC A,", "SUB ", "SBC A,", "AND ", "XOR ", "OR ", "CP ",
    };

    /// <summary>3-bit rotate/shift op rot[y]: 0=RLC 1=RRC 2=RL 3=RR 4=SLA 5=SRA
    /// 6=SLL (undocumented) 7=SRL. Matches <c>Z80.ApplyCbRotate</c>.</summary>
    public static readonly string[] Rot = { "RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL" };

    /// <summary>ED z=6 interrupt-mode text im[y]: y=0/1/4/5 all set IM 0 on real
    /// silicon (1 and 5 are the undocumented duplicate, rendered "0/1"); y=2/6 set
    /// IM 1; y=3/7 set IM 2. Index matches <c>Z80.ImByY</c> exactly.</summary>
    public static readonly string[] Im = { "0", "0/1", "1", "2", "0", "0/1", "1", "2" };

    /// <summary>ED block-instruction mnemonics, indexed [z, repeatIndex] where z
    /// selects the LD/CP/IN/OUT family (0-3) and repeatIndex selects the y=4..7
    /// variant (0=I non-repeat/increment, 1=D non-repeat/decrement, 2=IR
    /// repeat/increment, 3=DR repeat/decrement). Matches <c>Z80.ExecuteBlock</c>'s
    /// dispatch on (y,z). Note the OUT family's repeat forms are OTIR/OTDR, not
    /// OUTIR/OUTDR.</summary>
    public static readonly string[,] BlockOps =
    {
        { "LDI", "LDD", "LDIR", "LDDR" },
        { "CPI", "CPD", "CPIR", "CPDR" },
        { "INI", "IND", "INIR", "INDR" },
        { "OUTI", "OUTD", "OTIR", "OTDR" },
    };
}
