namespace P2000.Machine.Devices.Saa5050;

/// <summary>
/// The SAA5050's 256-entry gamma-corrected BGRA palette (`docs/SAA5050-implementation.md`
/// §4), indexed by <c>(background &lt;&lt; 5) | (foreground &lt;&lt; 2) | pixel</c> where
/// <c>pixel</c> is a 2-bit anti-aliased coverage level (0, 1/3, 2/3, 1) produced by the
/// rounding pass in <see cref="Saa5050Generator"/>. Gamma-blending the coverage rather than
/// hard-switching is what makes the rounded diagonals look smooth instead of jagged - this
/// table is shared (computed once, process-wide) since it depends on nothing but fixed
/// physical constants.
/// </summary>
internal static class Saa5050Palette
{
    internal static readonly uint[] ColorTable = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        const double gamma = 1.0 / 2.2;

        for (var color = 0; color < 256; color++)
        {
            var alpha = (color & 3) / 3.0; // 0, 1/3, 2/3, or 1

            var fR = (color & 4) != 0 ? 1 : 0;
            var fG = (color & 8) != 0 ? 1 : 0;
            var fB = (color & 16) != 0 ? 1 : 0;
            var bR = (color & 32) != 0 ? 1 : 0;
            var bG = (color & 64) != 0 ? 1 : 0;
            var bB = (color & 128) != 0 ? 1 : 0;

            var blendR = (byte)(Math.Pow(fR * alpha + bR * (1.0 - alpha), gamma) * 240);
            var blendG = (byte)(Math.Pow(fG * alpha + bG * (1.0 - alpha), gamma) * 240);
            var blendB = (byte)(Math.Pow(fB * alpha + bB * (1.0 - alpha), gamma) * 240);

            table[color] = (uint)(0xFF << 24 | blendB << 16 | blendG << 8 | blendR);
        }

        return table;
    }
}
