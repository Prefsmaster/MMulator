namespace P2000.UI.Rendering;

/// <summary>
/// Display presentation mode (project CLAUDE.md §8). All four modes read the same machine
/// framebuffer; only present-cadence, which-rows, and line-doubling differ.
/// </summary>
public enum DisplayMode
{
    /// <summary>Default: present every 50 Hz field, no inter-field clear → authentic interlace comb.</summary>
    Interlaced,

    /// <summary>Present only after the odd field (25 Hz) — both fields composited, no comb.</summary>
    Progressive,

    /// <summary>Present even field only, line-doubled to fill 480 lines.</summary>
    EvenOnly,

    /// <summary>Present odd field only (CRS smoothed sub-scanlines), line-doubled to fill 480.</summary>
    OddOnly,
}
