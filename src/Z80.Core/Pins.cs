namespace Z80.Core;

/// <summary>
/// Constants and helpers for the packed 64-bit pin mask that is the CPU's sole interface
/// to the outside world (floooh z80.h style). All control lines are represented
/// active-high in this mask (bit set = asserted), even though the real chip's pins are
/// active-low. This polarity is kept consistent everywhere in this codebase.
/// </summary>
public static class Pins
{
    // Address bus, bits 0-15.
    public const int AddressBits = 0;
    public const ulong AddressMask = 0xFFFFUL << AddressBits;

    // Data bus, bits 16-23.
    public const int DataBits = 16;
    public const ulong DataMask = 0xFFUL << DataBits;

    public const ulong M1 = 1UL << 24;
    public const ulong MREQ = 1UL << 25;
    public const ulong IORQ = 1UL << 26;
    public const ulong RD = 1UL << 27;
    public const ulong WR = 1UL << 28;
    public const ulong RFSH = 1UL << 29;
    public const ulong HALT = 1UL << 30;
    public const ulong WAIT = 1UL << 31;
    public const ulong INT = 1UL << 32;
    public const ulong NMI = 1UL << 33;
    public const ulong RESET = 1UL << 34;
    public const ulong BUSRQ = 1UL << 35;
    public const ulong BUSAK = 1UL << 36;

    public static ushort GetAddress(ulong pins) => (ushort)((pins & AddressMask) >> AddressBits);

    public static ulong SetAddress(ulong pins, ushort address) =>
        (pins & ~AddressMask) | ((ulong)address << AddressBits);

    public static byte GetData(ulong pins) => (byte)((pins & DataMask) >> DataBits);

    public static ulong SetData(ulong pins, byte data) =>
        (pins & ~DataMask) | ((ulong)data << DataBits);
}
