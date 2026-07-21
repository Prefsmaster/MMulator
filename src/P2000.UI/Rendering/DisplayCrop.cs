namespace P2000.UI.Rendering;

/// <summary>
/// Full-Field vs Graphics-window (project CLAUDE.md §8, 2026-07-22) — a SECOND toggle,
/// ORTHOGONAL to <see cref="DisplayMode"/>: that picks the field SOURCE (which scanlines/
/// cadence); this picks how much of the resulting full-field raster to crop for display.
/// </summary>
public enum DisplayCrop
{
    /// <summary>Default: the familiar 640×480 active-picture crop, no visible change for
    /// existing users.</summary>
    GraphicsWindow,

    /// <summary>The complete 928×626 raster including blanking margins — what a real P2000 +
    /// PAL TV also only partially displays as "active video," normally hidden by CRT overscan.
    /// Authenticity/debug viewing, not the everyday view.</summary>
    FullField,
}
