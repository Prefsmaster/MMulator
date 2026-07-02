namespace Z80.Disassembler;

/// <summary>One decoded instruction: where it starts, how many bytes it consumes,
/// its raw encoding, and its rendered text (mnemonic + operands, symbols applied
/// when a symbol/port lookup was supplied to <see cref="Disassembler.Decode"/>).</summary>
public readonly record struct DisasmLine(ushort Address, int Length, byte[] Bytes, string Text);
