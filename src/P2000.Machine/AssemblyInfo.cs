using System.Runtime.CompilerServices;

// Grants the test project visibility into internal video-renderer types (Saa5050Generator,
// the glyph tables, the palette) so milestone 5's golden-glyph tests can exercise the
// generator directly instead of only through the public Video/Machine surface.
[assembly: InternalsVisibleTo("P2000.Machine.Tests")]
