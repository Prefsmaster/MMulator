# P2000.UI — Findings Log Archive

Full historical findings-log entries archived from `CLAUDE.md` (P2000.UI project) §18,
during the 2026-07-24 sync-and-trim pass. Every entry here was cross-checked against
`P2000T-reference.md` first — several "Synced: no" flags turned out to be stale (the
content was already in the reference doc from an earlier pass, just never marked); two
genuine small gaps (the Port 0x06 bit 7 umlaut/diaeresis correction, and the (5,0) key's
clear-line/envelope function pair) were found missing and added to `P2000T-reference.md`
§5f during this same pass, and their entries' flags corrected to reflect that. See the live
project CLAUDE.md §18 for entries still open or from the last few active days, plus a
pointer back here for full history.

This file is a complete, unedited historical record — for posterity/audit, not day-to-day
reference. Nothing here should be treated as a currently-open task; check the live CLAUDE.md
§18 and the project's own milestone list (§14) for what's still outstanding.

---

### 2026-07-23 — Milestone 14 IMPLEMENTED: Disk drive UI
- **Assumed (per the milestone's own text):** the Config window already had an
  "Internal-slot board" selector (§7 lists it as an existing axis) — the disk drive axis would
  just slot in alongside it.
- **Found (a real, blocking pre-existing gap, not assumed correctly):** `ConfigWindowVm`/
  `ConfigWindow.axaml` had NO board selector at all — `BuildConfig()` never set `Board`, so
  every machine built from the config window was permanently `InternalBoard.None`. Without
  fixing this, a "Floppy drives" axis would have been unreachable (the FDC only exists when
  `Board == FloppyRam`). Added the missing selector (None/RAM-only/Floppy+RAM) as a genuine
  prerequisite, not scope creep — the milestone's own spec assumed it already existed.
- **Found (a second real, latent bug, surfaced by the same gap):** `ConfigWindowVm.Apply()` had
  no try/catch around `_runner.Reconfigure(config)`. `Machine`'s constructor throws
  `ArgumentException` for `FloppyRam` + non-T102 (and, since milestone 20, for an invalid
  `FloppyDrives` shape) — with no board selector, this combination was previously unreachable
  from the UI at all, so the gap was latent. Adding the board selector makes it reachable, so
  fixed it: `Apply()` now catches `ArgumentException` and surfaces it via `StatusMessage`
  instead of crashing the UI thread. Also proactively prevented the specific known-invalid
  combination: selecting `FloppyRam` auto-forces `RamVariant.T102` and disables the RAM
  selector (`CanEditRamVariant`) so a user can't build that combination through normal
  interaction either way — the try/catch is defense-in-depth, not the primary guard.
- **Design choice — config window models drive COUNT, not the machine's more general per-drive
  shape:** `MachineConfig.FloppyDrives` allows arbitrary indices/gaps/per-drive `Enabled`
  flags (machine milestone 20), but the UI only ever needs "how many drives, sequential from
  0." `ConfigWindowVm.FloppyDriveCount` + `ObservableCollection<FloppyDriveRowVm>` (resized to
  match, each row fixed at construction to its `DriveIndex`) is the whole axis — simpler than
  exposing the machine's full generality, and it's still a strict subset (every config this
  window can produce is valid input to `Machine`, just not every config `Machine` accepts is
  reachable from here). `LoadFromCurrentConfig`/`LoadCfgAsync` collapse a loaded config's drive
  list the same way (highest enabled index + 1 = count) — a hand-edited `.cfg` with gaps or a
  disabled middle drive round-trips lossily through this window, which is an accepted
  limitation of the simpler model, not a bug.
- **Machine-layer additions needed (small, additive — the "live status row" the milestone's own
  test (d) requires had no public accessor to read from):** `Upd765` gained `MotorOn` (the
  single shared control-latch bit), `GetCylinder(int drive)` (already-tracked per-drive state,
  just not exposed), and `CurrentTransfer` (a `TransferStatus?` snapshot of drive/head/
  direction during an active semi-DMA transfer, null when idle) — all host-status-only, none
  consulted by the chip's own command dispatch. Confirmed via the "check before adding" rule:
  neither the chip nor `DskImage` already exposed these.
- **Scoped OUT of the live status row, flagged rather than guessed:** "sector" — `Upd765`
  doesn't persist a current-sector value outside an active transfer's own command bytes (which
  aren't retained as separate fields), and adding that would be new state, not just a new
  accessor over existing state. "Head" is shown only during an active transfer (from
  `CurrentTransfer`); there's no persistent per-drive head register to show it from when idle,
  matching real hardware (H is a per-command parameter, not a resting register). Both flagged
  in `DiskDriveVm`'s own doc comments rather than fabricated.
- **NOT built this pass (explicitly out of scope — user asked for milestone 14 only):**
  milestone 14a (unsaved-changes eject/replace warning) — `DskImage.IsDirty`/`MarkClean()` and
  `MdcrDevice.IsDirty`/`MarkClean()` already exist from machine milestone 20a and
  `WriteDiskToFileAsync`/cassette's own save path already call `MarkClean()` on success, so
  14a has its machine-layer signal ready to consume, nothing here blocks it. Also not built:
  drag-drop of `.dsk` onto the main display window (ambiguous which drive should receive it
  with N drives configured, unlike the cassette's single-deck case — needs an owner decision on
  the default target before it can be built without guessing) and any UI-side persistence for
  disk write-protect (machine-layer M20 flagged this as blocked on a still-open "what does a
  saved session persist" question).
- **Tests:** `DiskDriveVmTests` (new, 15) — mount/eject/new-blank/write-protect state
  transitions and `CanExecute` wiring, write-protect actually gating a write, motor state
  shared identically across two drives' rows, per-drive independence (mounting on drive 0
  doesn't touch drive 1). `DiskDriveWindowVmTests` (new, 4) — row collection rebuilds on a
  topology `Reconfigure` (board added/removed, drive count changed), disabled drives get no
  row. `ConfigWindowVmTests` (new, 9 — this VM had NO tests before this pass) — board/RAM
  auto-force interaction, drive-count row resize (grow/shrink preserves earlier rows), config
  round-trip through `LoadFromCurrentConfig`, `Apply`'s try/catch. `Upd765Tests` (+7, machine
  layer) — the three new accessors. Uses `[AvaloniaFact]` + async/`Start()`/`await Task.Delay`
  for any test that needs a real `Reconfigure` swap to land (same requirement already
  documented in `EmulationRunnerStateTests`) — unlike `CassetteDeckVmTests`, which never
  reconfigures the machine's board and could stay fully synchronous. Full `P2000.UI.Tests`:
  124/124 green (was 99); `P2000.Machine.Tests`: 465/465 green (was 459).
- **Verified:** the app launches cleanly with this change (smoke-tested via a background
  launch + window-title check, no crash, main window title "MMulator - P2000T" present) but
  the actual Config→Floppy+RAM→Disk-Drives-window click-through was NOT driven end-to-end from
  this seat (no interactive access to a native Avalonia window) — same limitation already
  logged elsewhere in this file for computer-use against a running dev instance. Owner should
  click through: Config → Board = Floppy+RAM → set drive count → Apply → Disk menu → Open Disk
  Drives window → Mount/New/Save/Eject/write-protect per row.
- **Applies to:** project CLAUDE.md §14 milestone 14 /
  `src/P2000.Machine/Devices/Fdc/Upd765.cs` (`MotorOn`, `GetCylinder`, `CurrentTransfer`,
  `TransferStatus`), `src/P2000.UI/ViewModels/ConfigWindowVm.cs` (`Board`, `Boards`,
  `CanEditRamVariant`, `ShowFloppyDrives`, `FloppyDriveCount`, `FloppyDriveRows`,
  `FloppyDriveRowVm`, `Apply` try/catch), `src/P2000.UI/ViewModels/ConfigConverters.cs`
  (`InternalBoardDescConverter`, `DiskSidesDescConverter`), `src/P2000.UI/Views/ConfigWindow.axaml`
  (board selector, floppy-drives section), `src/P2000.UI/ViewModels/DiskDriveVm.cs` (new),
  `src/P2000.UI/ViewModels/DiskDriveWindowVm.cs` (new), `src/P2000.UI/Views/DiskDriveWindow.axaml(.cs)`
  (new), `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (`DiskVm`, `OpenDiskDriveWindowRequested`,
  `OpenDiskDrivesCommand`), `src/P2000.UI/Views/DisplayWindow.axaml(.cs)` (Disk menu, window
  wiring), `tests/P2000.Machine.Tests/Devices/Fdc/Upd765Tests.cs`,
  `tests/P2000.UI.Tests/ViewModels/DiskDriveVmTests.cs` (new),
  `tests/P2000.UI.Tests/ViewModels/DiskDriveWindowVmTests.cs` (new),
  `tests/P2000.UI.Tests/ViewModels/ConfigWindowVmTests.cs` (new).
- **Synced:** no (implementation-only — no new hardware facts; the scope-out decisions above
  are UX/sequencing calls, not corrections to anything the reference doc claims).

### 2026-07-22 — Flag (not yet implemented): Full-Field vs Graphics-window UI toggle
- **Trigger — owner's request:** the machine should render the complete field (black blanking
  margins included), and the UI should get an option to show "Full-Field" or "Graphics window
  only" — see `src/P2000.Machine/CLAUDE.md` §17 (2026-07-22 entry) and reference doc §3a/§4a
  for the full geometry derivation and design shape.
- **Found (scope confirmation, same pattern as the 2026-07-21 display-mode-default entry
  below):** this is a second, orthogonal UI-owned toggle, not a machine setting — the machine
  produces the full raster unconditionally; the UI decides how much to crop. No machine-layer
  mode needed for this either.
- **Owner review round 1 (before implementation) — two corrections, both resolved before any
  code was touched:**
  1. **Do not revert the dual even/odd field rendering machinery** — see the "IMPORTANT,
     owner-confirmed 2026-07-22" note on the four-display-mode entry above, and machine
     CLAUDE.md §17's WITHDRAWN note. No rendering-code change here, default-value only.
  2. **Full-field width corrected from 1024 to 928 px** — the owner's retrace model (chip
     emits nothing for 6 char-times at the start of each line; trailing blank left intact)
     excludes horizontal retrace from the buffer entirely. Crop rectangle offset is now
     (144, 98), not (240, 98). See machine CLAUDE.md §17 and reference doc §4a for the full
     derivation and the flagged 5-vs-6-char-time ambiguity.
- **Not yet done:** `DisplayMode.cs` / `DisplayControl.cs` / `DisplayWindowVm.cs` need the new
  toggle, the `WriteableBitmap` sizing needs to follow whichever crop is active (928×626 or
  640×480), and the `CorruptionOverlay` draw path needs a coordinate offset when Full-Field is
  active (overlay indices are relative to the 640×480 active window, not the full buffer) —
  this is a flag for Claude Code, not a confirmed implementation.
- **Applies to:** reference doc §3a (Full-Field vs Graphics-window) / `src/P2000.UI/Rendering/
  DisplayMode.cs`, `src/P2000.UI/Rendering/DisplayControl.cs`,
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs`.
- **Synced:** yes (2026-07-22, into P2000T-reference.md §3a) — implementation-side change still
  outstanding.

### 2026-07-22 — IMPLEMENTED: Full-Field/Graphics-window crop toggle + Odd-only default (closes both flags below/above)
- **`DisplayCrop` enum** (new file `Rendering/DisplayCrop.cs`): `GraphicsWindow` (default) /
  `FullField`. `DisplayControl.Crop` reallocates its backing `WriteableBitmap` to the crop's
  pixel size on change; `DisplayWindowVm.Crop` is the bindable VM-side property (default
  `GraphicsWindow`, with `IsCropGraphicsWindow`/`IsCropFullField`/`SetCropCommand` following the
  exact same pattern as the existing 4-way `DisplayMode`).
- **Corruption overlay offset — resolved the handoff's own open implementation choice
  ("offset at draw time, or store overlay full-buffer-sized — both are fine") in favour of
  offset-at-draw-time:** `DrawCorruptionOverlay` computes the active window's own origin as a
  sub-rect of `_destRect`, adding `ActiveOffsetX/Y` (scaled to destRect units) only when
  `Crop == FullField`; zero offset in `GraphicsWindow` since the whole destRect already IS the
  active window. No change to the overlay's own storage shape (stays 40×24, machine-side).
- **PAL aspect — implemented as "always letterbox using the crop's own true aspect ratio when
  Full-Field, regardless of the PalAspect toggle's value," not a silent no-op:** added
  `DisplayWindowVm.CanTogglePalAspect` (`Crop == GraphicsWindow`), bound to the View-menu item's
  `IsEnabled` so the toggle visibly greys out in Full-Field rather than doing nothing invisibly.
  `DisplayControl.ComputeDestRect`'s letterbox branch now fires on `PalAspect || Crop ==
  FullField` — for Full-Field this produces native-pixel-geometry letterboxing (928:626 isn't
  4:3, so this is genuinely different math from the Graphics-window PAL correction, not the same
  branch reused coincidentally).
- **Display-mode default flip (closes the 2026-07-21 flag below) — confirmed TWO separate
  defaults needed changing, not one:** `DisplayControl.Mode` and `DisplayWindowVm._displayMode`
  are independent fields with their own `= DisplayMode.Interlaced` initializers; both flipped to
  `DisplayMode.OddOnly`. Per the owner-confirmed "default-value change only" instruction, no
  per-field rendering code was touched. The View menu's "(default)" label moved from the
  Interlaced entry to the Odd-only entry.
- **Screenshot updated to respect the current crop** (`DisplayWindowVm.Screenshot()`) — it
  previously always serialized the full machine buffer unconditionally; now crops exactly like
  `DisplayControl.CopyToWriteableBitmap` does, using the same offset math.
- **Not done this pass (tooling limitation):** could not get computer-use to attach to an
  ad-hoc `dotnet run`-launched dev window for a live visual check (it only resolves
  Start-Menu-registered/tracked apps). Verified via `P2000.UI.Tests` (97, including 5 new
  `DisplayWindowVmTests`) + full `P2000.Machine.Tests` (401) instead. Flagging so a future pass
  does the actual eyes-on-screen check (see the parallel entry in `src/P2000.Machine/CLAUDE.md`
  §17 for the specific checklist).
- **Applies to:** `src/P2000.UI/Rendering/DisplayCrop.cs` (new),
  `src/P2000.UI/Rendering/DisplayControl.cs`, `src/P2000.UI/ViewModels/DisplayWindowVm.cs`,
  `src/P2000.UI/Views/DisplayWindow.axaml(.cs)`, `src/P2000.UI/Runner/EmulationRunner.cs` (doc
  comments only), `tests/P2000.UI.Tests/ViewModels/DisplayWindowVmTests.cs` (new).
- **Synced:** yes (2026-07-21, implementation-only — confirmed no reference-doc action needed;
  the crop/display-mode design facts were already synced into the reference doc before this
  pass).

### 2026-07-21 — Flag (not yet verified): display-mode default should change to Odd-only
- **Trigger:** owner-supplied P2000TM Field Service manual states, for the T-version: *"the
  signal CRS is active during the even scanlines of the field. In our system we use only the
  odd scanlines, so no interlacing is used."* Confirmed correct by the owner. See
  `src/P2000.Machine/CLAUDE.md` §17 (2026-07-19/21 entries) and `docs/SAA5050-implementation.md`
  §5 for the full hardware-timing correction (real T hardware has no even/odd field pairing;
  every field is an independent 313-line refresh).
- **Found (scope confirmation):** this project's own 2026-07-07 milestone-6 finding below
  already correctly built the four display modes as a pure UI-presentation layer over the
  machine's raw per-field events (`FieldComplete`/`IsOddField`) — no machine changes needed.
  Only the DEFAULT selection needs revisiting.
- **Owner decision, 2026-07-21:** default should move from **Interlaced (comb)** to
  **Odd-only** (mode 4, line-doubled single field) — it's the mode that matches the FSM's "only
  the odd scanlines, no interlacing." Interlaced/comb remains available as a legitimate
  opt-in/nostalgia mode, just no longer presented as authentic-default T behaviour.
- **Not yet done:** the actual default value in `DisplayMode.cs` / `DisplayWindowVm.cs`
  (milestone 6, below) has not been checked or changed in this pass — this is a flag for
  Claude Code, not a confirmed fix.
- **Applies to:** reference doc §3a (display mode) / `src/P2000.UI/Rendering/DisplayMode.cs`,
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs`.
- **Synced:** yes (2026-07-21, into P2000T-reference.md §3a) — implementation-side change still
  outstanding.

### 2026-07-07 — Milestone 4: cassette deck + CLOAD end-to-end
- **Assumed:** the `.cas` tape block structure was MARK + HEADER (32 B) + DATA (1024 B) as three
  separate WriteData frames. See `src/P2000.Machine/CLAUDE.md` §17 (2026-07-07) for the full
  root-cause analysis and Cassette.asm trace.
- **Found (root bug — machine layer):** the correct structure is MARK + ~81 ms gap +
  combined HEADER+DATA in one frame with one CRC. Without the gap, `read_until_timeout` reads
  into the HEADER frame after the MARK → `paddingbytes != 0` → `search_marker_loop` retry →
  eventual 'N'/'M' error. Fixed in `MiniTape.LoadCasImage` and `Save()`.
- **Found (byte order confirmed LSB-first):** the ROM byte assembler is `rr d`
  (Cassette.asm:1140), not `rla` (which is CRC-only). 0xAA is the correct sync byte.
- **Found (CassetteDeckVm — live reference pattern):** `CassetteDeckVm` reads
  `_runner.Machine.Mdcr` / `_runner.Machine.CpOut` on every `FrameReady` tick rather than
  caching the device reference. This automatically stays correct after `Reconfigure()` swaps
  the machine since it dereferences through `_runner.Machine` each time.
- **Applies to:** project CLAUDE.md §14.4 (milestone 4) / `src/P2000.Machine/CLAUDE.md` §17 /
  `src/P2000.Machine/Devices/Cassette/MiniTape.cs`,
  `src/P2000.UI/ViewModels/CassetteDeckVm.cs`.
- **Synced:** yes (2026-07-10 — tape block structure in docs/MDCR-implementation.md §6 + reference §5b; already reflected)

### 2026-07-07 — Milestone 5: config window + .cfg load/save
- **Assumed:** `EmulationRunner.Machine` could remain a get-only property for the lifetime of
  the app; a topology change would require restarting the process or a separate factory.
- **Found (Reconfigure swap pattern):** a volatile `_nextMachine` field + `SemaphoreSlim`
  lets the UI thread build a new machine and block (~20 ms max) while the emulation thread
  acknowledges the swap at the next field boundary. No lock needed: the volatile write is the
  signal; the semaphore is purely for the UI thread to wait for acknowledgement. The old
  machine's `FieldComplete` and `BreakHit` are unsubscribed inside the swap on the emulation
  thread so there is no race between the old event firing and the new machine taking over.
- **Found (BreakHit forwarding):** `DisplayWindowVm` previously subscribed to
  `Runner.Machine.BreakHit` directly (hard reference to the original machine). After
  `Reconfigure` that subscription would silently stop working. Fixed by adding a forwarding
  `Action<BreakEvent> BreakHit` event on `EmulationRunner` that re-routes across swaps;
  `DisplayWindowVm` now subscribes to the runner, not the machine.
- **Found (status bar model text):** `ModelText` was computed as
  `config.Model.ToString().Replace("P2000","")` → always "T" regardless of RAM variant.
  Updated to "T/38", "T/54", "T/102" by appending the `RamVariant` suffix.
- **Found (ConfigWindow as satellite, not modal):** opening as a non-modal `Show(this)`
  satellite (same pattern as `CassetteDeckWindow`) is preferable to `ShowDialog` — the user
  can still interact with the emulator display while the config window is open.
- **Applies to:** project CLAUDE.md §14.5 (milestone 5) /
  `src/P2000.UI/Runner/EmulationRunner.cs` (`Reconfigure`, `BreakHit` forwarding),
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (`ModelText`, `OpenConfigCommand`),
  `src/P2000.UI/ViewModels/ConfigWindowVm.cs`,
  `src/P2000.UI/Views/ConfigWindow.axaml`.
- **Synced:** yes (2026-07-10 — non-modal-satellite decision in UI §5; Reconfigure/ModelText items implementation-only)

### 2026-07-07 — Milestone 1: app shell + emulation loop + display blit
- **Assumed:** `AppBuilder.WithInterFont()` was a standard Avalonia 11.1 extension.
- **Found:** `WithInterFont()` requires a separate `Avalonia.Fonts.Inter` package not included
  in the base `Avalonia 11.1.0` / `Avalonia.Desktop` / `Avalonia.Themes.Fluent` trio. Dropped
  the call — the system font renders fine and the emulator display doesn't depend on it.
- **Found:** `P2000.Machine` is both a namespace AND contains a class named `Machine`.
  Using `using P2000.Machine;` in the runner causes `CS0118: 'Machine' is a namespace but is
  used like a type`. Resolution: alias `using MachineCore = P2000.Machine.Machine;` in files
  that need the class directly. Other files (VM, App) only reference it through the runner
  and are unaffected.
- **Found (AXAML rule):** `xmlns:` declarations must appear on the root element. Moving the
  `xmlns:local` for the `ViewLocator` inline element out to `<Application ...>` fixed
  `AXN0002`.
- **Found (boot screen confirmed):** the "PHILIPS MICROCOMPUTER P2000" splash screen IS the
  ROM's cassette-wait state — the ROM polls CIP in a loop displaying this screen until a
  tape is mounted. Validation gate §13.1 confirmed (bare machine, display rendering, 50 Hz).
- **Applies to:** project CLAUDE.md §14.1 (milestone 1) /
  `src/P2000.UI/Program.cs`, `src/P2000.UI/App.axaml`,
  `src/P2000.UI/Runner/EmulationRunner.cs`,
  `src/P2000.UI/Rendering/DisplayControl.cs`.
- **Synced:** yes (2026-07-10 — implementation-only, no reference change)

### 2026-07-07 — Milestone 6: display modes + video prefs
- **Assumed:** `FrameReady` could remain `Action<uint[]>` for all consumers; mode switching
  was a pure rendering concern inside `DisplayControl`.
- **Found (`fieldWasOdd` timing):** when the runner's `OnFieldComplete` fires, `Video.IsOddField`
  has ALREADY toggled to the next field's parity. The field that just completed = `!IsOddField`.
  This value gates Progressive (present only after odd field = both interlaced fields done),
  EvenOnly (present only after even field), and OddOnly (present only after odd field).
- **Found (corruption overlay must be copied at field boundary):** `Video.CorruptionOverlay` is
  cleared by the machine AFTER `FieldComplete` returns (Video.cs line 152: `Array.Clear` after
  the event). The runner's `OnFieldComplete` runs inside the event, so the overlay is still
  populated when the copy occurs. Must copy it in the runner alongside the framebuffer — not
  deferred to the UI thread callback where it would already be cleared.
- **Found (`FrameReady` signature widened):** changed from `Action<uint[]>` to
  `Action<uint[], bool, bool[]>` (pixels, fieldWasOdd, corruptionSnapshot). Both
  `CassetteDeckVm` and `DisplayWindowVm` updated; the code-behind handler pushes video prefs
  from the VM to `DisplayControl` on every frame (cheap property writes at 50 Hz).
- **Found (`EnumEqualsConverter` needed for AXAML radio-button pattern):** menu `IsChecked`
  for the four display-mode items binds `DisplayMode` property against a `{x:Static}` enum
  value via `EnumEqualsConverter`. Added to `StatusConverters.cs`.
- **Found (`DisplayControl.Background` not supported):** `Control` doesn't expose `Background`
  without explicitly adding an `AvaloniaProperty`. Removed from AXAML; window background
  already `Black` so no visual change.
- **Applies to:** project CLAUDE.md §14.6 (milestone 6) /
  `src/P2000.UI/Rendering/DisplayMode.cs` (new),
  `src/P2000.UI/Rendering/DisplayControl.cs` (modes, scaling, scanlines, overlay),
  `src/P2000.UI/Runner/EmulationRunner.cs` (FrameReady signature, corruption copy),
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (video prefs),
  `src/P2000.UI/Views/DisplayWindow.axaml` (View menu),
  `src/P2000.UI/Views/DisplayWindow.axaml.cs` (FrameReady wiring),
  `src/P2000.UI/Views/StatusConverters.cs` (EnumEqualsConverter).
- **Synced:** yes (2026-07-10 — IsOddField-at-FieldComplete in reference §3a, overlay-clear in §4; converter items implementation-only)

### 2026-07-09 — Milestone 7: audio (OpenAL beeper sink)
- **Assumed:** `Silk.NET.OpenAL` 2.21.0 would expose managed `ref`/`out`/array overloads for
  `GenSources`, `GenBuffers`, `BufferData`, etc. (as some versions do).
- **Found:** 2.21.0 exposes ONLY raw unsafe pointer overloads. All call sites must use `unsafe`
  methods with `fixed` blocks for managed arrays. Stack-local `uint` variables (source ID,
  freed buffer ID, single-buffer queuing) can be addressed with `&local` directly in an
  `unsafe` context without `fixed` (value types on the stack are not GC-moveable).
  Array elements accessed inside a loop must be copied to a stack local first
  (`uint bid = buffers[i]; al.SourceQueueBuffers(source, 1, &bid);`) — nesting a second
  `fixed (&buffers[i])` inside an existing `fixed` block or attempting `fixed` on a loop
  variable triggers CS0213.
- **Found (AudioEngine design):** 4-buffer OpenAL streaming source. Background thread at 5 ms
  poll: dequeues processed buffers, refills with PCM from `ConcurrentQueue<short[]>` (or
  silence on starvation), re-queues. Restarts source on starvation stop. Mute/volume driven by
  lazy `_gainDirty` flag to avoid redundant AL calls.
- **Found (SoundDevice.SamplesReady buffer ownership):** `SoundDevice` reuses its internal
  `short[]` buffer immediately after `SamplesReady` returns. `AudioEngine.EnqueueSamples` must
  copy before enqueuing; it does so with `Array.Copy`.
- **Applies to:** project CLAUDE.md §14.7 (milestone 7) /
  `src/P2000.UI/Audio/AudioEngine.cs` (new),
  `src/P2000.UI/Runner/EmulationRunner.cs` (Audio wiring),
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (AudioMute/AudioVolume),
  `src/P2000.UI/Views/DisplayWindow.axaml` (View > Mute audio menu item).
- **Synced:** yes (2026-07-10 — SoundDevice audio seam in reference §5 Sound; OpenAL/pointer items implementation-only)

### 2026-07-07 — Milestone 4 addendum: cassette directory — full header fields
- **Assumed:** the directory only needed the 8-char name (header bytes 06-0D).
- **Found (tape header structure, from `docs/MDCR/Tape Header.md`):** the 32-byte block header
  carries the full 16-char filename split across two 8-byte fields (bytes 06-0D + 17-1E),
  a 3-char extension (bytes 0E-10), a 1-byte creator ID (byte 11), file size as a LE word
  (bytes 04-05), and a block counter (byte 1F). The directory should show all of these.
- **Found (block count from bytes 02-03):** header bytes 02-03 hold the space occupied on tape
  (may be larger than the file if a shorter file was written over a longer one). Divide by 1024
  to get blocks occupied. Header byte 1F ("blocks remaining") is a write-time counter, not used.
- **Found (format — monospaced columns):**
  `{name,-16} {.ext,-4} {creator,-2} {size,8} {blocks,4}` with Dutch-style dot thousands
  separator for file size (e.g. `24.331`). Header row bound via `DirectoryHeader` static
  property on the VM; window widened to 440 px to accommodate the extra columns.
- **Applies to:** project CLAUDE.md §14.4 (milestone 4 addendum) /
  `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`ParseDirectory`, `DirectoryHeader`),
  `src/P2000.UI/Views/CassetteDeckWindow.axaml`.
- **Synced:** yes (2026-07-10 — 32-byte header field table in docs/MDCR-implementation.md §6 + §8 directory de-dup)

### 2026-07-09 — Milestone 8: save-state UI
- **Assumed:** `SaveState` / `LoadState` could be called directly from the UI thread at any time.
- **Found (instruction-boundary guarantee):** `MachineStateFile.Save` must be called at an
  instruction boundary. The safe mechanism is a volatile `_pendingSaveStream` field checked
  in `EmulationRunner.OnFieldComplete()` (runs on the emulation thread), identical to the
  existing `Reconfigure` swap pattern. The UI thread sets the stream, then waits on a
  `SemaphoreSlim` (~20 ms max); the emulation thread saves and releases.
- **Found (`ReconfigureWithMachine` added):** `Reconfigure(MachineConfig)` always builds a
  fresh machine. For load-state, `MachineStateFile.Load` already returns a complete
  `Machine`; a `ReconfigureWithMachine(Machine)` overload wires the events and swaps it in
  via the same volatile `_nextMachine` / `_swapDone` mechanism.
- **Found (`AvaloniaHeadlessPlatformOptions` not `AvaloniaHeadlessOptions`):** the correct
  Avalonia 11.1.0 headless options type for `AppBuilder.UseHeadless(...)` in test projects
  is `AvaloniaHeadlessPlatformOptions` (from `Avalonia.Headless`). The `AvaloniaTestApplicationAttribute`
  is in `Avalonia.Headless`; `[AvaloniaFact]` is in `Avalonia.Headless.XUnit`.
- **Applies to:** project CLAUDE.md §14.8 (milestone 8) /
  `src/P2000.UI/Runner/EmulationRunner.cs` (`SaveStateToStream`, `ReconfigureWithMachine`),
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (`SaveStateCommand`, `LoadStateCommand`),
  `src/P2000.UI/Views/DisplayWindow.axaml` (Machine menu items),
  `src/P2000.UI/Views/DisplayWindow.axaml.cs` (`ShowErrorDialog`),
  `tests/P2000.UI.Tests/` (new project, 6 tests).
- **Synced:** yes (2026-07-10 — save-at-instruction-boundary already in reference §3a; the .state version-bump gap this exposed was fixed later — see the v1→v2 finding below; reference §3a now RESOLVED)

### 2026-07-10 — Milestone 9: debugger observer core
- **Assumed:** `[ObservableProperty] private string _af` would generate a property named `AF`.
- **Found:** CommunityToolkit.Mvvm source generator capitalises the first letter only (`_af` → `Af`,
  not `AF`). All two-letter register acronyms (AF, BC, DE, HL, IX, IY, SP, PC, WZ, IFF1, IFF2, IM)
  must be written as manual properties with `SetProperty` to keep the public names readable.
- **Found (corruption overlay geometry):** `Video.CorruptionOverlay` is 40×24 (one bool per
  visible viewport column, not per VRAM column). Index = `row × 40 + viewportCol` where
  `viewportCol = vramCol − PanX`. The VRAM grid control maps each absolute VRAM column to a
  viewport column before checking the corruption flag.
- **Found (live memory follow — best-effort):** `Machine.Cpu.Reg.HL` etc. are readable outside
  a snapshot (direct struct access). This is racy mid-instruction but acceptable for the live
  "follow register" display in memory watches. Snapshot-based reads (at break/step) are exact.
- **Found (`VramGridControl` — `AffectsRender` + property replacement):** binding arrays
  (`byte[]`, `bool[]`) to styled properties only triggers `InvalidateVisual` when the array
  reference changes (Avalonia uses reference equality). `VramWindowVm.Update` always allocates
  new arrays, which satisfies this. `AffectsRender<VramGridControl>(...)` wires all four
  properties so any change auto-invalidates without manual override of property changed.
- **Found (VRAM refactored to satellite window — post-ship):** the VRAM grid was inlined
  in `DebuggerWindow`'s right panel, leaving no room for milestone 10 content. Extracted
  to `VramWindow` (same satellite pattern as `MemoryWatchWindow`): `DebuggerWindowVm` fires
  `OpenVramWindowRequested`; code-behind opens/reuses `VramWindow { DataContext = Vram }`.
  `DebuggerWindow` now has registers on the left and a blank right panel ready for
  disassembly/breakpoints/stepping (milestone 10). The `VramWindowVm` and all VRAM update
  paths are unchanged.
- **Found (live register display — post-ship fix):** registers only updated from
  `OnBreakHit`, so the panel showed "–" for all registers while the machine was running.
  Fix: `DebuggerWindowVm.OnFrameReady` now calls `RegisterFile.UpdateLive(m.Cpu.Reg,
  m.Video.FieldTState)` on every frame when not paused. `RegisterFileVm.UpdateLive` reads
  directly from the `Registers` struct and derives flags from the `F` byte via bitmask —
  no snapshot needed. Values are best-effort (sampled at field boundary), exact only at
  break/step.
- **Applies to:** project CLAUDE.md §14.9 (milestone 9) /
  `src/P2000.UI/ViewModels/RegisterFileVm.cs` (manual properties, `UpdateLive`),
  `src/P2000.UI/ViewModels/MemoryWatchVm.cs`, `VramWindowVm.cs`, `DebuggerWindowVm.cs`,
  `src/P2000.UI/Rendering/VramGridControl.cs`,
  `src/P2000.UI/Views/DebuggerWindow.axaml`, `MemoryWatchWindow.axaml`,
  `src/P2000.UI/Views/DisplayWindow.axaml` (Debug menu),
  `tests/P2000.UI.Tests/ViewModels/` (22 new tests).
- **Synced:** yes (2026-07-10 — corruption-overlay viewport-column indexing into reference §4; MVVM/AffectsRender items implementation-only)

### 2026-07-10 — Milestone 10: disassembly + breakpoints + stepping
- **Assumed:** `BreakHit` would already fire for single-step, PauseCommand, and
  run-to-scanline/cycle completions so the UI could take a snapshot after stepping.
- **Found (silent-pause gap):** `SingleStepCommand`, `PauseCommand`, and `_runToFieldTState`
  all set `IsPaused = true` without firing `BreakHit`. The UI subscribed only to `BreakHit`,
  so stepping appeared to do nothing (registers didn't update, disassembly didn't refresh).
  **Fix:** added `BreakpointKind.Step` to the enum (id = -1, no real breakpoint) and wired
  `BreakHit?.Invoke(new BreakEvent(BreakpointKind.Step, Cpu.Reg.PC, -1))` in all three
  silent-pause paths in `Machine.cs`.
- **Found (`DisassemblyVm` in-place update):** `ObservableCollection` fires `CollectionChanged`
  on every `Add`/`Remove`, causing ListView item recycling flicker on every decode. Fix:
  overwrite existing `DisassemblyLineVm` items in-place (mutate properties) and only
  add/remove at the tail to reach the right count.
- **Found (breakpoint management — no IDs needed):** `BreakpointStore` assigns sequential IDs
  but the command queue is fire-and-forget (IDs not returned to caller). UI maintains a
  `HashSet<ushort> _execBpSet`; on toggle: clear all, re-add from the set. The full queue
  drains atomically at one instruction boundary, so the clear+re-add is race-free.
- **Found (disassembly live refresh throttle):** re-decoding on every `FrameReady` (50 Hz) is
  wasteful when PC hasn't moved. `DisassemblyVm.NeedsRefresh(pc)` compares against
  `_lastPc`; only re-decodes when PC has changed.
- **Found (Avalonia 11 visual-tree walk):** `Avalonia.Visual.VisualParent` no longer exists.
  Walking up the tree to find a `DataContext` must use `.Parent as Control` instead.
- **Applies to:** project CLAUDE.md §14.10 (milestone 10) /
  `src/P2000.Machine/Debug/BreakpointKind.cs` (`Step` value),
  `src/P2000.Machine/Machine.cs` (BreakHit in 3 silent-pause paths),
  `src/P2000.UI/P2000.UI.csproj` (Z80.Disassembler reference),
  `src/P2000.UI/ViewModels/DisassemblyLineVm.cs` (new),
  `src/P2000.UI/ViewModels/DisassemblyVm.cs` (new),
  `src/P2000.UI/ViewModels/DebuggerWindowVm.cs` (stepping cmds, breakpoints, disassembly),
  `src/P2000.UI/Views/StatusConverters.cs` (BoolToPcBrushConverter, BoolToBpDotConverter),
  `src/P2000.UI/Views/DebuggerWindow.axaml` (stepping toolbar + disassembly panel),
  `src/P2000.UI/Views/DebuggerWindow.axaml.cs` (OnDisasmTapped breakpoint toggle).
- **Synced:** yes (2026-07-10 — BreakHit-on-all-pause-transitions into reference §3a + machine §3b; disasm/breakpoint-UI items implementation-only)

### 2026-07-10 — .state format version bump: v1 → v2 (retroactive)
- **Assumed (at milestone 8, when Save/Load State shipped):** `MachineStateFile.CurrentVersion`
  would be bumped as each format-changing machine milestone landed. Two changes were explicitly
  flagged as "bumping deferred" but never actually bumped:
  - Milestone 12: `InterruptAggregator.SaveState` grew from 1 bool to 2 (`_intPending` +
    `_nmiPending`).
  - Milestone 16: `SoundDevice` block inserted between `Mdcr` and `Interrupts` in
    `Machine.SaveState/LoadState`.
- **Found (silent mis-load risk):** `CurrentVersion` was still 1; the reader accepted v1 files
  (`version >= 1 && version <= 1`), but the device stream was fatally misaligned —
  `Sound.LoadState` consumed the old single-bool Interrupts payload, then `Interrupts.LoadState`
  read garbage or hit EOF. There was no exception until stream underrun.
- **Fix:** `CurrentVersion = 2`; `MinVersion = 2`; reader rejects v1 files with an
  `InvalidDataException` ("Unsupported .state version 1. This build supports versions 2–2.")
  rather than silently loading corrupt state. `Load_VersionOne_Throws` test added.
- **No migration path:** no external `.state` files were distributed; any saves produced
  during milestones 11–15 testing should be discarded.
- **Applies to:** `src/P2000.Machine/State/MachineStateFile.cs` (`CurrentVersion`, `MinVersion`,
  version-gate check), `tests/P2000.Machine.Tests/State/MachineStateFileTests.cs`
  (`Load_VersionOne_Throws`).
- **Synced:** yes (2026-07-10 — into reference §3a: version-bump note updated PENDING/⚠ → RESOLVED (v2))

### 2026-07-10 — Audio: OpenAL Soft native DLL + queue-cap latency fix
- **Assumed:** `openal32.dll` would be present on the developer's machine (many Windows
  systems have it via games). It was not — `Silk.NET.OpenAL` threw `FileNotFoundException`
  on init and audio was silently absent.
- **Found (native DLL bundling):** Silk.NET.OpenAL is a pure P/Invoke binding with no
  bundled native library. The correct cross-platform solution is to ship the platform DLL
  alongside the app via the .NET `runtimes/<rid>/native/` convention.
  - `tools/get-openal.ps1` downloads OpenAL Soft 1.23.1 Win64 (`soft_oal.dll`) from
    GitHub releases and places it as `runtimes/win-x64/native/openal32.dll`.
  - `P2000.UI.csproj` uses `<Content … Link="openal32.dll">` with `IsOSPlatform` guards
    to copy the right DLL to the output root at build time (`Link=` overrides the default
    `runtimes/` subfolder preservation that `<None>` would give).
  - Linux: `libopenal.so.1` from system packages; macOS: system OpenAL framework.
  - The `runtimes/` folder is committed to git (binary DLL included) so CI/team members
    don't need to run the script.
- **Found (startup latency ~1.3 s):** `alc.OpenDevice("")` on Windows blocks for ~1 s on
  first call (device enumeration / driver init). The emulation thread produces 50 blocks/s
  into `_queue` during this time. No size cap meant ~60 stale silence-blocks queued ahead
  of the first audible beep. Fix: `MaxQueueDepth = 6` in `EnqueueSamples` drops oldest
  blocks when the queue exceeds ~120 ms depth. Combined with 4 OpenAL buffers × 20 ms =
  80 ms, total latency is capped at ~200 ms regardless of init time.
- **Applies to:** project CLAUDE.md §14.7 (milestone 7, audio) /
  `src/P2000.UI/Audio/AudioEngine.cs` (MaxQueueDepth, doc update),
  `src/P2000.UI/P2000.UI.csproj` (native content items),
  `src/P2000.UI/runtimes/win-x64/native/openal32.dll` (bundled binary),
  `tools/get-openal.ps1` (download script).
- **Synced:** yes (2026-07-10 — OpenAL native-DLL bundling + queue-cap latency: deployment/implementation-only, no reference change)

### 2026-07-14 — Milestone 12: debugger memory watch export/import
- **Assumed:** the data-flow check in this milestone's own spec (§14.12) — export via the
  existing snapshot memory-read surface, import via the existing `LoadImageCommand` — would
  hold with no machine-side gap. Confirmed true: `LoadImageCommand(ushort StartAddress, byte[]
  Data)` (`src/P2000.Machine/Debug/MachineCommand.cs`) already writes an arbitrary-length byte
  array at an arbitrary address via a plain `Memory.Write` loop — no cartridge/image-shape
  assumption, no machine change needed.
- **Found (design decision — `MemoryWatchVm` now takes the runner):** `MemoryWatchVm` was
  previously constructible with no arguments (a pure observer fed externally via `Update()`).
  Export/import needs to enqueue a command, so the constructor now takes `EmulationRunner` —
  same pattern as `CassetteDeckVm`/`ConfigWindowVm` (store the runner, dereference
  `_runner.Machine` at call time so a `Reconfigure()` swap is picked up automatically, per the
  milestone-5 finding). `DebuggerWindowVm.AddMemoryWatch()` updated to pass `_runner`; existing
  `MemoryWatchVmTests` updated to construct via `new MemoryWatchVm(new EmulationRunner())`.
- **Found (top-of-RAM guard is a UI-side check, not a machine one):** `PageTable.Write` silently
  discards out-of-range/unpopulated writes (open-bus convention) rather than throwing, so
  "reject a file that would run past 0xFFFF" cannot come from the machine. `LoadFileToAddressAsync`
  checks `address + data.Length > 0x10000` before enqueuing and surfaces a message instead —
  the only bound checked (the file may exceed the watch window's own configured length since
  the target address is independent of the window's range).
- **Found (dialog plumbing reused verbatim):** `SaveFilePickerAsync`/`OpenFilePickerAsync` +
  `TopLevel` lookup + a `ShowMessageRequested` event forwarded to a small dialog in
  code-behind — same shape as `DisplayWindowVm`'s save/load-state commands and
  `CassetteDeckVm`'s mount. No new UI pattern introduced.
- **Applies to:** project CLAUDE.md §14.12 /
  `src/P2000.UI/ViewModels/MemoryWatchVm.cs` (constructor, `SaveRangeToFileAsync`,
  `LoadFileToAddressAsync`, `LoadAddressText`, `ShowMessageRequested`),
  `src/P2000.UI/ViewModels/DebuggerWindowVm.cs` (`AddMemoryWatch`),
  `src/P2000.UI/Views/MemoryWatchWindow.axaml` (toolbar actions),
  `src/P2000.UI/Views/MemoryWatchWindow.axaml.cs` (`ShowErrorDialog`),
  `tests/P2000.UI.Tests/ViewModels/MemoryWatchVmTests.cs` (+6 tests).
- **Synced:** no (implementation-only — no hardware/spec correction to sync; the machine-side
  `LoadImageCommand` contract itself was already synced at machine ms.15).

### 2026-07-14 — Milestone 12 follow-up: configurable watch range (owner feedback)
- **Assumed (first pass above):** a fixed 256-byte window was enough — "export" would just dump
  whatever the window happened to be displaying (`_curr`), and "import" only needed an editable
  target address.
- **Found (owner feedback — not hardware, a scope correction):** a fixed 256-byte range isn't
  actually useful for pulling an arbitrary machine-code routine out of RAM (the milestone's own
  motivating case). Two changes:
  1. **The watch window itself is now range-configurable**, not fixed at 256 bytes. Added a
     "Length" field (hex) next to "Base"; `MemoryWatchVm.SetRange(ushort, int)` sets both
     together, clamps length to `[1, 0x10000]`, and resizes `Rows` (now an
     `ObservableCollection<MemoryWatchRow>`, not a fixed 16-element array) plus the internal
     `_curr`/`_prev` buffers to `ceil(length/16)` whole rows. A length not a multiple of 16
     rounds the display up (the extra trailing bytes are real memory, just past the requested
     length) — only the exact requested length is used for export.
  2. **"Save range to file…" now prompts for its own start+length**, defaulting to the window's
     current `BaseAddress`/`Length` but independently editable at save time — so a one-off
     export doesn't require changing what the window is currently watching. Implemented as a
     small ad-hoc modal in `MemoryWatchWindow.axaml.cs` (`PromptRangeAsync`, same
     inline-`Window`-construction style as the existing `ShowErrorDialog`), then calling
     `MemoryWatchVm.SaveRangeToFileAsync(start, length)` — no longer a `[RelayCommand]` bound
     directly to the button, since the view needs to gather the range first.
- **Found (export now reads live machine memory directly, not `_curr`):** since the save-time
  range can differ from the window's own displayed range, `SaveRangeToFileAsync` reads
  `_runner.Machine.Memory.Read` fresh for the requested range rather than reusing `_curr` (which
  is sized/addressed to the window's own configured range). This is actually simpler than the
  first pass and still satisfies "live-at-that-moment" for both paused and running, since it's a
  direct read at click time either way.
- **Found ("Go" now sets both base and length):** `OnGoClicked` reads both `AddressBox` and the
  new `LengthBox` and calls `vm.SetRange(...)` once. Leaving the length box's parsed value equal
  to `vm.Length` when the box is empty preserves the old "just navigate" behaviour.
- **Found (StorageProvider dialogs remain untested at the unit level):** confirmed no existing
  test in this suite drives `SaveFilePickerAsync`/`OpenFilePickerAsync` end-to-end (checked
  `DisplayWindowVm.SaveStateAsync`/`LoadStateAsync` — also untested for the same reason); a
  headless `[AvaloniaFact]` test has no real desktop `TopLevel`/`MainWindow`, so `GetTopLevel()`
  returns null and the picker call never fires. Kept the same scope here: unit tests cover
  `SetRange`'s resize/clamp behaviour and the underlying `Machine.Memory.Read`/`Write` +
  `LoadImageCommand` paths those commands rely on, not the file-picker plumbing itself.
- **Applies to:** project CLAUDE.md §14.12 /
  `src/P2000.UI/ViewModels/MemoryWatchVm.cs` (`Length`, `SetRange`, `ResizeBuffers`, `Rows` now
  `ObservableCollection<MemoryWatchRow>`, `SaveRangeToFileAsync` signature),
  `src/P2000.UI/Views/MemoryWatchWindow.axaml` (Length field, Save button now `Click`-bound),
  `src/P2000.UI/Views/MemoryWatchWindow.axaml.cs` (`OnGoClicked`, `OnSaveRangeClicked`,
  `PromptRangeAsync`),
  `tests/P2000.UI.Tests/ViewModels/MemoryWatchVmTests.cs` (range tests added; export tests
  reworked around `Machine.Memory.Read`/`Write` instead of the old fixed-buffer helper).
- **Synced:** yes (2026-07-14, this merge pass — folded into §10's memory watch bullet: range is
  now explicitly configurable via a Length field, and "Save range to file…" prompts for its own
  independently-editable start+length rather than always matching the window's live display
  range. Also synced (2026-07-14, follow-up pass) into `docs/P2000T-reference.md` §3a's
  "Memory watch windows" bullet — the canonical home for this per this file's own header — which
  additionally needed its stale "Read-only; never touches the live core" claim CORRECTED: export
  is read-only, but import is a real (queued, boundary-safe) RAM write, not an exemption from the
  "every mutation is a queued command" rule. That bullet had never been synced for milestone 12
  at all until now — this pass covers both the original export/import addition and this
  range-configurable follow-up in one go.).

### 2026-07-14 — Milestone 13: cassette deck — New (blank) tape + Save/Save-as wiring
- **Assumed:** per the milestone's own "verify, don't assume" instruction — that
  `MdcrDevice` might already have a live blank-mount entry point equivalent to `InsertTape()`.
  Confirmed it did not: `InsertTape(byte[] casImage, ...)` is the only mount path and always
  parses a real `.cas` byte stream via `MiniTape.LoadCasImage`.
- **Found (reported back, then authorized and implemented — see `P2000.Machine/CLAUDE.md`
  §17, 2026-07-14 "DECIDED"/"IMPLEMENTED" pair):** `MdcrDevice.InsertBlankTape()` added.
  Turned out to need NO `MiniTape` change at all — its existing parameterless constructor
  already produces exactly the required blank state (BOT, unprotected, zero blocks,
  pseudo-noise-filled). `InsertBlankTape()` is a two-line method that swaps `_tape` directly
  (never through `null`), which is also what gives "one CIP transition, not two" for free —
  no eject-then-insert logic needed.
- **Found (Save-vs-Save-as backing tracked as `IStorageFile?`, not a raw path string):**
  `CassetteDeckVm` now holds `_backingFile` (Avalonia's `IStorageFile`, not a `string` path) —
  the same object returned by `OpenFilePickerAsync`/`SaveFilePickerAsync`/drag-drop, reusable
  directly for `OpenWriteAsync()` on a plain "Save" without re-resolving a path. Null after
  `NewBlankTape()`; set on file-dialog mount, drag-drop mount, and after a successful
  "Save as…". `MountBytes` gained an optional `IStorageFile? backingFile` parameter (default
  null preserves old callers); `DisplayWindow.axaml.cs`'s drag-drop handler now passes the
  dropped `IStorageFile` through instead of just its bytes+name.
- **Found (Save/SaveAs `CanExecute` reuses the `nameof(HasTape)` pattern):** same shape as
  `DebuggerWindowVm`'s `[RelayCommand(CanExecute = nameof(IsPaused))]` — `HasTape` is already
  an `[ObservableProperty]` bool, so `SaveCommand`/`SaveAsCommand` gate on it directly with no
  separate predicate method, and `OnHasTapeChanged` (already existed, extended) keeps both in
  sync alongside the existing `EjectCommand` notify.
- **Found (`SaveTape()` needed no machine change — the milestone's own "clean" half):**
  `MdcrDevice.SaveTape()`/`MiniTape.Save()` already existed from ms.9a and are host-side/
  always-fast/independent of `TimingPolicy`, exactly as the spec assumed — pure UI wiring
  (`WriteTapeToFileAsync`) on top.
- **Found (StorageProvider dialogs remain untested at the unit level — same limitation as
  milestone 12):** `CassetteDeckVmTests` (new) covers state transitions
  (`HasTape`/`TapeLabel`/`IsWriteProtected`/`Programs`), the Save/SaveAs `CanExecute` wiring,
  and — via the real `MdcrDevice` the VM drives — that mounting a blank tape over an
  already-mounted one never observes CIP go absent. The actual file-picker halves of
  `MountAsync`/`SaveAsync`/`SaveAsAsync` are not unit-tested (no real desktop `TopLevel` in a
  headless run); the byte-identical blank→CSAVE→Save→reload round trip is instead tested at
  the machine layer (`MdcrDeviceTests`, new — exercises `WriteBlockAtHead`/`SaveTape`/
  `InsertTape`/`TryReadBlockAtHead` directly), which is where it's actually testable.
- **Not built (per the milestone's own explicit scope):** no "format tape" affordance (blank
  tape is immediately writable, confirmed no format step exists); no dedicated "Erase" UI
  (a running program's own erase is indistinguishable from any other CSAVE, already visible
  via the activity LED + directory); rewind and write-protect toggle remain
  decided-but-unbuilt, untouched here.
- **Applies to:** project CLAUDE.md §14.13 / `P2000.Machine/CLAUDE.md` §17 (the
  `InsertBlankTape` decision + implementation entries) /
  `src/P2000.Machine/Devices/Cassette/MdcrDevice.cs` (`InsertBlankTape`),
  `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`NewBlankTape`, `SaveAsync`, `SaveAsAsync`,
  `WriteTapeToFileAsync`, `_backingFile`, `MountBytes` signature, `ShowMessageRequested`),
  `src/P2000.UI/Views/CassetteDeckWindow.axaml` (New/Save/Save-as buttons),
  `src/P2000.UI/Views/CassetteDeckWindow.axaml.cs` (`ShowErrorDialog`),
  `src/P2000.UI/Views/DisplayWindow.axaml.cs` (drag-drop passes `IStorageFile` through),
  `tests/P2000.Machine.Tests/Devices/MdcrDeviceTests.cs` (+6 tests),
  `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs` (new — 8 tests).
- **Synced:** no (implementation-only UI/machine wiring; the one hardware-adjacent fact —
  "no format step, blank tape immediately writable" — was already confirmed with the owner
  and recorded in the milestone spec itself, nothing new to sync).

### 2026-07-14 — Milestone 13a: cassette deck — write-protect toggle
- **Reported symptom (owner):** the cassette always reads as write-protected, no UI to change
  it. **Root cause confirmed by inspection, exactly as milestone 13's own reported-symptom
  matched the machine CLAUDE.md §17 "DECIDED" entry predicted:** `CassetteDeckVm.MountBytes`
  hardcoded `_runner.Machine.Mdcr.InsertTape(casImage, writeProtect: true)` on every
  file-loaded mount — `InsertBlankTape()` was correctly unprotected (as ms.13's own tests
  already showed) because it never took the parameter at all. Nothing was "stuck" by tape
  content; the one caller simply never asked for anything else.
- **Fix (breaking API change, authorized in `P2000.Machine/CLAUDE.md` §17):**
  `MdcrDevice.InsertTape`/`MiniTape.LoadCasImage` no longer take a `writeProtect` parameter —
  protection is now read from the `.cas` file itself (record offset `0x50`, bit 0 — see the
  machine-layer entry for the full container-format decision) and round-trips through
  `Save()`. `CassetteDeckVm.MountBytes` reads `IsWriteProtected` back FROM the machine after
  mount instead of assuming a value.
- **Found (live toggle needed no new plumbing beyond the setter itself):** made
  `CassetteDeckVm.IsWriteProtected`'s existing `[ObservableProperty]` two-way bindable (a
  `CheckBox` in the deck window replacing the old passive 🔒 `TextBlock`) and added
  `OnIsWriteProtectedChanged` pushing to the new `MdcrDevice.SetWriteProtected(bool)` whenever
  `HasTape` — no separate `[RelayCommand]` needed since the property setter IS the command
  here (CommunityToolkit's changed-hook fires on both user interaction and our own internal
  post-mount sync, and re-pushing the same value is harmless/idempotent).
- **Found ("Rewind" reclassified, not a peer of write-protect):** while investigating this
  symptom the owner confirmed the real MDCR has no rewind button at all (only Eject) — a
  correction to §3.1's earlier phrasing, which had listed "rewind" alongside write-protect as
  a peer decided-but-unbuilt item. It isn't a deferred control; there's nothing to build.
- **Tests:** `MdcrDeviceTests`/`MiniTapeTests` (machine layer, see `P2000.Machine/CLAUDE.md`
  §17 for the full list) plus `CassetteDeckVmTests` (+4 at the VM level): the regression check
  (a plain record with no protect byte defaults writable), a protect-byte-set record mounts
  protected, the live toggle flips the real `MdcrDevice`'s WEN without touching CIP, and
  toggling with no tape mounted doesn't throw.
- **Applies to:** project CLAUDE.md §14.13a / `P2000.Machine/CLAUDE.md` §17 (the
  write-protect DECIDED/IMPLEMENTED pair — full container-format detail lives there) /
  `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`MountBytes`, `OnIsWriteProtectedChanged`),
  `src/P2000.UI/Views/CassetteDeckWindow.axaml` (write-protect `CheckBox`),
  `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs` (+4 tests).
- **Synced:** no (implementation-only; the "rewind has no physical control" correction is a
  UI-doc-only wording fix, not new hardware content — reference doc §5b already only
  describes rewind as a host-API convenience, not a physical button).

### 2026-07-14 — Milestone 13a follow-up: padlock click-to-toggle (owner feedback)
- **Reported:** the `CheckBox` toggle gave mixed signals — a checkmark AND a lock glyph AND
  text, three signals not obviously in sync at a glance.
- **Fix:** replaced the `CheckBox` with a borderless, transparent-background `Button` whose
  content is one bigger padlock glyph (22px, was 11px inline) + a label, both driven by the
  SAME `IsWriteProtected` bool via two new converters — `BoolToPadlockIconConverter` (🔒/🔓)
  and `BoolToWriteProtectLabelConverter` ("Write protected"/"Write enabled") — so there is
  exactly one signal (open vs. closed padlock) reinforced by matching text, no separate
  checkmark to contradict it.
- **Found (no new VM plumbing needed):** added `ToggleWriteProtectCommand`
  (`IsWriteProtected = !IsWriteProtected`) purely so the Button has something to bind
  `Command` to — the actual machine push still happens through the existing
  `OnIsWriteProtectedChanged` hook (§14.13a above), unchanged. Considered `ToggleButton`
  first but a plain `Button` + command avoids fighting the Fluent theme's own checked-state
  chrome (which would have reintroduced a second, competing visual signal).
- **Tests:** `CassetteDeckVmTests` (+2) — command disabled with no tape, enabled once mounted;
  executing it flips both `IsWriteProtected` and the live `MdcrDevice` state back and forth.
- **Applies to:** `src/P2000.UI/Views/StatusConverters.cs` (`BoolToPadlockIconConverter`,
  `BoolToWriteProtectLabelConverter`), `src/P2000.UI/Views/CassetteDeckWindow.axaml` (padlock
  `Button` replacing the `CheckBox`), `src/P2000.UI/ViewModels/CassetteDeckVm.cs`
  (`ToggleWriteProtectCommand`), `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs`
  (+2 tests).
- **Synced:** no (pure UI/UX polish, no hardware or architectural content).

### 2026-07-14 — CSAVE bug fix confirmed live; directory list didn't refresh after CSAVE
- **Owner-confirmed (live app, after the `P2000.Machine/CLAUDE.md` §17 blank-tape-silence
  fix):** the reported "Cassette fout N" CSAVE failure is resolved. Full round trip tested
  live: CLOAD from a real tape → eject → insert blank → CSAVE → save `.cas` → mount → CLOAD
  again — success. A second CSAVE (different name) onto the tape just loaded from also
  succeeded (search, rewind, save, rewind, search, validate). Replace (same name) and
  tape-full scenarios not yet tested by the owner.
- **Owner-supplied ROM source (`Cassette.asm`, the monitor's cassette driver) reviewed —
  important correction to earlier speculation:** the replace-vs-append decision in
  `cas_block_write` is driven entirely by two `cassette_status` RAM bits (`CST_NOMARK`,
  `CST_WCDON`) carried over from whatever cassette operation ran immediately before
  `cas_Write` — there is **no filename comparison inside `Cassette.asm` at all**. The
  "search for a file starting with the same letter, check it fits in the allocated block
  count" policy the owner described must live in BASIC's own save routine (which positions
  the tape and primes these bits before calling into this low-level ROM driver) — a
  different, higher-level source this project does not have. This means the earlier
  session's `AuthenticCassetteWriteTests` (all constructed as fresh `Machine()`s, so
  `cassette_status` = 0 = `CST_NOMARK` clear) likely exercised the **replace** path on their
  first `cas_Write` call — i.e. probably overwrote the target tape's first found block rather
  than genuinely appending a new file — even though every test only asserted `A==0`
  (success) and never checked *where* the write landed. Flagging honestly rather than
  claiming those tests validated "append" behavior they may not have. Confirmed-good gap
  understanding, not a machine bug: `Cassette.asm` is the low-level tape mechanic only;
  BASIC's file-management policy on top of it is out of scope without that source.
- **Found (owner-reported UI gap, fixed):** the cassette deck's program/directory list never
  refreshed after a live CSAVE (typed in BASIC) — it was only ever built once, from the bytes
  passed to `MountBytes` at mount time. A CSAVE mutates the tape's live bitstream directly
  through `MdcrDevice`/`MiniTape`; the VM had no way to know. Fixed by re-deriving the
  directory on the falling edge of `IsActive` (motor just stopped — covers both CLOAD and
  CSAVE finishing) via `_runner.Machine.Mdcr.SaveTape()` (the same host-side serializer
  "Save"/"Save as…" already use) → `ParseDirectory`, instead of relying on the mount-time
  snapshot.
- **Found (not unit-tested, documented rather than silently skipped):** verifying this
  specific fix needs a genuine "motor on then off" transition on a *live, running* `Machine`.
  A live machine actively runs the embedded ROM, which drives CPOUT itself (keyboard scan,
  its own CIP-triggered auto-load attempt on a freshly-mounted tape) — any CPOUT value a test
  forces gets overwritten by ordinary ROM execution within the same field, independent of
  which thread calls `Tick()`/`RunField()` (this isn't a threading race, it's the ROM's own
  deterministic execution), and `Machine.Tick()` fully no-ops while paused so pausing first
  doesn't help either. No clean way to synthesize this edge without driving a real CSAVE/
  CLOAD through BASIC. Same gap already existed for this class's `HasTape` 5 Hz resync,
  which was never unit-tested either. Verified instead by the owner's live-app test above.
- **Applies to:** `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`RefreshDirectoryFromLiveTape`,
  `_wasActive`, `OnFrameReady`), `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs`
  (documented the untested gap rather than shipping a flaky test).
- **Synced:** yes (2026-07-14 — the UI directory-refresh fix itself is implementation-only, but
  the `Cassette.asm` replace/append finding this entry cites turned out worth syncing after
  all — see `P2000.Machine/CLAUDE.md` §17's matching entry and P2000T-reference.md §5b
  "Replace vs append," now corrected).

### 2026-07-14 — Directory list block count off by one (floor vs ceiling division)
- **Owner-reported (live app, replace-scenario testing):** the "Blk" column undercounted —
  5673 occupied bytes showed 5 blocks (should be 6); 40 occupied bytes showed 0 blocks
  (should be 1).
- **Root cause:** `ParseDirectory`'s `blocks = occupied / 1024` was a floor division. The
  milestone-4 finding that documented this field ("space occupied on tape... divide by 1024
  to get blocks occupied") never established that `occupied` is always exactly
  block-aligned — and per the owner-supplied `Cassette.asm`'s own `get_length_blocks`
  routine, it correctly is NOT: block count is always a ceiling of byte-length/1024 with a
  minimum of one block for any nonzero length (a partial trailing block still counts as a
  whole reserved block). Floor division silently undercounted by one whenever `occupied`
  wasn't an exact multiple of 1024.
- **Fix:** `blocks = (occupied + 1023) / 1024` — ceiling division, matching the ROM's own
  arithmetic. Exact multiples of 1024 are unaffected (verified by a dedicated test) since
  ceiling and floor agree there.
- **Distinct from, and does not change, the already-confirmed "block count doesn't shrink on
  a replace-with-shorter-file" behavior** (milestone-4 finding, re-confirmed live by the
  owner this session) — that's about what value `occupied` legitimately holds (the ORIGINAL
  allocation, preserved across a replace); this fix is purely about correctly converting
  whatever that byte value is into a block count for display.
- **Tests:** `CassetteDeckVmTests` (+2) — the owner's exact reported numbers (5673→6, 40→1)
  via a new `BuildDirectoryEntry` header-construction helper; a block-aligned case (1024→1)
  confirms the fix doesn't regress the already-correct exact-multiple path.
- **Applies to:** `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`ParseDirectory`),
  `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs` (+2 tests).
- **Synced:** no (implementation-only UI display fix; the underlying `Cassette.asm`
  `get_length_blocks` ceiling-division fact is machine-adjacent context, already noted in
  the entry above, not new hardware content).

### 2026-07-14 — Block count follow-up: byte 0x1F tried and ruled out empirically
- **Owner instruction:** block count should come straight from the header's own block-counter
  field (offset 0x1F, per the class doc comment's own long-standing "1F: block counter" note)
  rather than be derived arithmetically from the occupied-bytes field.
- **Tried:** switched `ParseDirectory` to `blocks = casImage[hdr + 0x1F]` directly.
- **Reverted — owner empirically confirmed byte 0x1F is unusable:** inspecting a real `.cas`
  file, that byte is always zero. Likely explanation (ties back to the milestone-4 finding
  this session already had on record but under-weighted: "byte 1F ('blocks remaining') is a
  write-time counter, not used"): `Cassette.asm`'s `block_counter` RAM variable counts DOWN
  during a multi-block transfer and is only meaningfully non-zero mid-transfer — whatever
  ends up in the on-tape header's copy of it is a transient snapshot, not a stable per-file
  total, and evidently lands on zero in practice. Reverted to the ceiling-division-on-
  occupied-bytes approach from the entry above (already validated against the owner's exact
  reported numbers), updating the class doc comment to explicitly rule out 0x1F with the
  empirical reason, so this isn't re-attempted without new evidence.
- **Tests:** reverted `CassetteDeckVmTests`' block-count tests back to the occupied-bytes
  `BuildDirectoryEntry` helper (ceiling-division assertions), matching the entry above.
- **Follow-up — mechanism now understood, confirms the revert was correct (owner, same day):**
  the "always zero" file was created by a different tool, not this emulator — files this
  emulator's own CSAVE writes DO show a live decrementing value at byte 0x1F across a
  multi-block file (6, 5, 4, 3, 2, 1 for a 6-block file), because the header transfer is
  literally "32 consecutive bytes from memory" (`des_length`=0x20 from a fixed RAM address,
  Cassette.asm) — whatever `block_counter`'s live value happens to be at that moment rides
  along incidentally, it isn't a deliberate per-file field. Per the owner's own reasoning
  (not yet source-confirmed against BASIC, which this project doesn't have): **reading**
  derives the block count fresh from a size-bearing field via arithmetic and ignores the
  stored byte entirely — i.e. the same shape as the current `ParseDirectory` fix. Different
  tools populate that incidental byte differently (explains the contradiction with the
  externally-created file), so it was correctly ruled out either way. No further code change
  from this follow-up — recorded so the byte-0x1F idea isn't revisited without new evidence.
- **Applies to:** `src/P2000.UI/ViewModels/CassetteDeckVm.cs` (`ParseDirectory` doc comment
  and `blocks` calculation), `tests/P2000.UI.Tests/ViewModels/CassetteDeckVmTests.cs`.
- **Synced:** no (implementation-only UI fix; documents a ruled-out approach for future
  reference, no new hardware content beyond what's already flagged above).

### 2026-07-09 — Integer scaling: physical vs logical pixels
- **Assumed:** computing the integer multiplier `n` from `Bounds.Width / Video.Width` (logical
  pixels) would produce exact integer multiples of source pixels on screen.
- **Found:** Avalonia `Bounds` are in logical (device-independent) pixels. At 125% Windows DPI
  (`RenderScaling = 1.25`), `n = floor(logicalWidth / 640) = 1` produces a `Rect` of 640
  logical units, which Avalonia renders as 800 physical pixels — not an integer multiple of the
  640×480 source (800 / 640 = 1.25).
- **Fix:** compute `n` in physical pixel space: `n = floor(logicalWidth × scale / 640)`, then
  convert back: `sw = n × 640 / scale` logical units → exactly `n × 640` physical pixels.
  At 125% DPI with a 640-logical-px panel, `n = 1` → 512 logical = 640 physical px.
- **Applies to:** project CLAUDE.md §14.6 (milestone 6, integer scaling) /
  `src/P2000.UI/Rendering/DisplayControl.cs` (`ComputeDestRect`).
- **Synced:** yes (2026-07-10 — DPI/physical-pixel integer scaling: rendering implementation-only, no reference change)

### 2026-07-19 — Milestone 3a: ms.3's "no host key at all" claim was wrong for the numpad function keys
- **Assumed (§14.3a, as drafted):** the numeric keypad's cassette/program-control keys (ZOEK,
  START, STOP, INL, OPN, DEF, and the tape/dsk/M icon keys) "have no host key to bind to at
  all, mapping mode aside" — implying the soft-keyboard window would be the first thing to
  make them reachable.
- **Found (false — ms.3 already wired all of them):** `src/P2000.UI/Input/KeyMap.cs`, sourced
  from `docs/Keyboard/keyboard matrix.md` (committed 2026-07-07, ms.3's own commit — never
  given a §18 findings entry, same "shipped correct but never logged" pattern already noted
  for SHIFT/digit-row in §14.3a), maps every one of these positions directly to the
  corresponding host NumPad key: NumPad0→DEF (2,3), NumPad1→ZOEK (7,3), NumPad2→flash (7,2),
  NumPad3→START (7,0), NumPad4→INL (8,3), NumPad6→OPN (8,0), NumPad7→tape (6,3),
  NumPad8→dsk (6,2), NumPad9→M (6,0), Decimal→STOP (2,0). CODE (4,0) is already mapped to
  LeftCtrl; Shift-Lock (3,0) to CapsLock. This is the same crosspoint the physical P2000 key
  serves for both plain numeric entry and its icon/function legend — one matrix position, ROM
  context decides the meaning — so ms.3's positional mapping already covers it correctly, no
  machine or KeyMap change needed.
- **Only two matrix positions remain host-unreachable** (known positions, just no host key
  bound): np00/TB (2,2) and np-center/tab/envelope (5,0). The soft-keyboard window can reach
  both directly by matrix coordinate since it enqueues positions, not host keys. **WIS** still
  has no matrix position sourced anywhere (open item, unchanged).
- **Revised scope for the soft-keyboard window (§14.3a):** it is a discoverability/no-numpad
  affordance and the only way to reach np00/TB, the envelope key, and (once sourced) WIS — not
  the first path to ZOEK/START/STOP/INL/OPN/DEF, which already work today from any keyboard
  with a numpad.
- **Applies to:** project CLAUDE.md §14.3a / `src/P2000.UI/Input/KeyMap.cs` /
  `docs/Keyboard/keyboard matrix.md`.
- **Synced:** no (corrects this project's own milestone doc; no reference-doc hardware content
  changes as a result).

### 2026-07-19 — Milestone 3a: soft-keyboard window + Standard-Host mode, built
- **Found (matrix correction, owner-caught):** `docs/Keyboard/keyboard matrix.md` and the new
  `docs/Keyboard/keyboard mappins.md` both mislabeled Port 0x06 bit 7 shifted as `"` — it's
  actually **¨ (umlaut/diaeresis dead key)**. The only key that produces `"` is Port 0x07 bit 7.
  Confirmed by re-photographing the physical key (shows two dots, not a double-quote glyph) and
  by the owner (who'd mistyped it in their own draft table). Fixed in both matrix files and
  `KeyMap.cs`'s comments — the matrix POSITION (`OemOpenBrackets` → (6,7)) was never wrong, only
  the documented character.
- **Found (photo re-verification caught a second transcription slip, mine this time):** my first
  pass at the soft-keyboard's visual layout misplaced the "'`" key (port 8 bit 4) and the "<>" key
  (port 3 bit 2) — I'd assumed "<>" sat on the top numeric row and "'`" didn't exist on the
  keyboard at all. Zoomed crops of `docs/Keyboard/P2000T keyboard.jpg` (via a scratch Python/PIL
  script — no image-crop tool exists in this toolchain) showed "'`" actually sits on the top row
  between `-_` and Backspace, and "<>" is the ISO-style key immediately left of Z (matching
  `KeyMap.cs`'s existing "ISO extra key" comment for `OemBackslash` → (3,2), which turned out to
  already be right — my new layout code was the one with the bug, not the shipped matrix).
- **Built:** `HostKeyTranslator` (`src/P2000.UI/Input/HostKeyTranslator.cs`) — stateful
  press/release bookkeeping shared by the physical-keyboard handler (`DisplayWindow.axaml.cs`)
  and the new soft-keyboard window, so both obey the same `KeyMappingMode`. `KeyMap.cs` gained
  `MapStandardHost` (an explicit override table derived algorithmically from the confirmed
  matrix — see `docs/Keyboard/keyboard mappins.md`'s "Standard-Host mode reverse mapping" — not
  hand-guessed) plus the `KeyMappingMode` enum. New `SoftKeyLayout.cs` (full photo-verified 10×8
  layout data), `KeyboardWindowVm`/`SoftKeyVm` (sticky Shift/CODE, toggle Lock, mode toggle,
  momentary regular-key press), `Views/KeyboardWindow.axaml(.cs)`.
- **Found (`DisplayWindow`'s `e.Handled` needed a real signal, not "always true"):** the
  pre-existing key handler only marked non-matrix keys (F5/F11/…) unhandled so the window's own
  `KeyBindings` still fired. Routing everything through the shared translator meant `KeyDown`/
  `KeyUp` had to start returning whether the key was recognized at all (in the current mode) —
  added that return value rather than hardcoding `Handled = true`.
- **Found (owner decisions, both confirmed live in this session, not guessed):** (1) the
  ambiguous `"` position — (7,7), not (6,7), per the umlaut correction above; (2) missing
  US symbols (`~^{}\|`) are a silent no-op in Standard-Host mode, matching the milestone's own
  "flag, don't guess" instruction rather than picking a wrong stand-in character.
- **Verified live (owner's running app, this session):** soft-keyboard click of "1" types `1`
  into BASIC; sticky Shift + click "8" in P2000-Authentic gives `(`; toggling to Standard-Host
  relabels the digit row (2→@, 3→#, 6→^, 7→&, 8→*, 9→(, 0→)) and Shift+8 now gives `*` instead —
  confirming both the translator's forced-shift bookkeeping and the mode-dependent legend
  refresh work end-to-end, not just at the unit level.
- **Tests (72 total in `P2000.UI.Tests`, all green):** `HostKeyTranslatorTests` — Authentic
  regression (Shift+8/Shift+2 unchanged), Standard-Host literal-character cases including both
  forced-shift-ON and forced-shift-OFF, the missing-symbol no-op, positional fallback, and OS
  auto-repeat suppression. `SoftKeyLayoutTests` — every `HostKey`-bearing position matches
  `KeyMap` exactly, the only `HostKey: null` positions are the three confirmed-unreachable ones
  (np00/TB, envelope, "#/°"), no duplicate matrix positions. `KeyboardWindowVmTests` — soft-click
  parity with the equivalent host key, sticky Shift latch/release (both click-again and
  release-after-one-key), CODE and Shift latch independently, mode toggle updates both the
  translator and displayed legends.
- **Not built (per the milestone's own scope):** CODE's semantic effect (modeled as a sticky
  modifier only, per the milestone's explicit "do NOT invent" instruction). **WIS's matrix
  position was resolved the same day — see the follow-up entry below — so it is NOT in this
  "not built" list**; it was always reachable (host `NumPad7`), only its shifted meaning was
  undocumented at the time this entry was first written.
- **Applies to:** project CLAUDE.md §14.3a / `src/P2000.UI/Input/KeyMap.cs`,
  `src/P2000.UI/Input/HostKeyTranslator.cs` (new), `src/P2000.UI/Input/SoftKeyLayout.cs` (new),
  `src/P2000.UI/ViewModels/KeyboardWindowVm.cs` (new), `src/P2000.UI/Views/KeyboardWindow.axaml(.cs)`
  (new), `src/P2000.UI/Views/DisplayWindow.axaml(.cs)` (menu item, key-handler rewiring),
  `src/P2000.UI/ViewModels/DisplayWindowVm.cs` (`KeyTranslator`, `KeyboardVm`),
  `src/P2000.UI/Views/StatusConverters.cs` (+3 converters), `docs/Keyboard/keyboard matrix.md`,
  `docs/Keyboard/keyboard mappins.md` (umlaut correction + finished Standard-Host table),
  `tests/P2000.UI.Tests/Input/*` (new), `tests/P2000.UI.Tests/ViewModels/KeyboardWindowVmTests.cs`
  (new).
- **Synced:** yes (2026-07-24 sync pass — the umlaut/diaeresis correction (Port 0x06 bit 7
  shifted = ¨, not `"`; real `"` is Port 0x07 bit 7) is now in `docs/P2000T-reference.md` §5f's
  "Confirmed keyboard facts"; the rest of this entry is implementation-only UI wiring).

### 2026-07-19 — Milestone 3a follow-up: WIS located (owner)
- **Assumed (per this milestone's own text above, and `docs/P2000T-reference.md` §6 "still to
  confirm"):** WIS's ROM-level function was known ("clear cassette dialog," §5b) but its matrix
  position had never been located anywhere — not in the owner's photo transcription, not
  independently derivable, open item.
- **Found (owner-confirmed):** WIS is **Shift + the numpad `7` key** (port 0x06 bit 3) — the key
  showing the mini-cassette-tape icon, already mapped in ms.3's `KeyMap.cs` to host `NumPad7`.
  Same shift-selects-the-function convention as ZOEK/START/STOP/INL/OPN/DEF elsewhere on the
  keypad (unshifted = the digit, shifted = the function) — no new matrix behaviour and no code
  change needed beyond the label: the key was always reachable and functional via Shift+NumPad7
  through the existing `HostKeyTranslator`, only its shifted MEANING was undocumented.
- **Changed:** `SoftKeyLayout.cs`'s (6,3) entry relabeled from generic "tape" to "WIS" (matching
  the ZOEK/START/STOP convention of showing the named function, not an icon description);
  `docs/Keyboard/keyboard matrix.md` and `docs/Keyboard/keyboard mappins.md` updated
  ("np 7 tape" → "np 7 tape/WIS").
- **Applies to:** project CLAUDE.md §14.3a (WIS paragraph, marked RESOLVED inline) /
  `src/P2000.UI/Input/SoftKeyLayout.cs` / `docs/Keyboard/keyboard matrix.md` /
  `docs/Keyboard/keyboard mappins.md`.
- **Synced:** yes (already present in `docs/P2000T-reference.md` §5f's "Confirmed keyboard
  facts" — "WIS — CONFIRMED position... matrix (6,3)"; this entry's own "Synced: no" was stale
  bookkeeping, caught and corrected during the 2026-07-24 sync-and-archive pass).

### 2026-07-19 — Milestone 3a follow-up: CODE's function confirmed (owner)
- **Assumed (per this milestone's own text and `docs/P2000T-reference.md` §6 "still to
  confirm"):** CODE's matrix position AND its actual function were both fully unsourced;
  speculated candidates were a second shift level or a graphics/block-character set (both
  common on similar-era keyboards) — neither ever confirmed, hence "do NOT invent."
- **Found (owner-confirmed):** CODE's effect is **not a fixed emulator-level behaviour at all —
  it's cartridge/software-dependent**, decided by whatever's plugged into SLOT1, not by the
  keyboard hardware. With the **BASIC cartridge** specifically, CODE is used to control
  **LIST display speed** and while **editing BASIC program lines**. Different cartridge
  software could use the same matrix bit differently.
- **Confirms (no change needed):** modeling CODE as a bare sticky modifier — pressing/releasing
  matrix position (4,0), no character-set or shift-level logic on the emulator side — was
  already the correct design, for the same reason SHIFT needs none: the ROM/cartridge reads the
  bit and decides what it means, exactly like it does for SHIFT. Nothing to build differently in
  `HostKeyTranslator`, `KeyMap`, or the soft-keyboard window.
- **Applies to:** project CLAUDE.md §14.3a (CODE-key paragraph, marked RESOLVED inline).
- **Synced:** yes (already present in `docs/P2000T-reference.md` §5f's "Confirmed keyboard
  facts" — the "CODE — CONFIRMED position (4,0)..." bullet covers exactly this finding; this
  entry's own "Synced: no" was stale bookkeeping, caught during the 2026-07-24 sync-and-archive
  pass).

### 2026-07-20 — Milestone 3a follow-up: (8,4) is accent aigu/grave, not apostrophe/backtick — real bug found
- **Assumed:** `KeyMap.cs`, `SoftKeyLayout.cs`, and both `docs/Keyboard/keyboard matrix.md` /
  `keyboard mappins.md` labeled Port 0x08 bit 4 as `'` (unshifted) / `` ` `` (shifted) —
  apostrophe and backtick.
- **Found (owner-corrected):** it's actually **´ (accent aigu, unshifted) / ` (accent grave,
  shifted)** — a diacritic pair, not a literal apostrophe. The backtick half was coincidentally
  already correct (accent grave IS what ASCII backtick conventionally represents); only the
  unshifted half was wrong.
- **Found (real Standard-Host bug, not just a label fix):** `KeyMap.MapStandardHost` had no
  override for `(Key.OemQuotes, false)`, so it fell back to the positional mapping — pressing
  (8,4) unshifted. With the corrected label, that means a plain host `'` in Standard-Host mode
  was sending the P2000's **accent aigu**, not an apostrophe. The P2000 DOES have a real
  apostrophe — (0,6) shifted (Shift+7, part of the original confirmed digit-row table) — so
  added `{ (Key.OemQuotes, false), new MatrixTarget(0, 6, true) }` to redirect there. Backtick's
  existing override ((8,4) shifted) was unaffected and needed no change.
- **Not a bug in P2000-Authentic mode:** Authentic is positional passthrough by design — sending
  the P2000's own accent-aigu key for that host position is correct there; only Standard-Host
  (which promises the literal host character) needed the fix.
- **Tests (+3, `HostKeyTranslatorTests`):** Standard-Host apostrophe redirects to (0,6) shifted;
  Standard-Host backtick still targets (8,4) shifted (regression guard); Authentic mode still
  sends the positional accent-aigu for the same host key (regression guard, not "fixed" — that
  behavior is correct as-is).
- **Applies to:** `src/P2000.UI/Input/KeyMap.cs` (comments + new override entry),
  `src/P2000.UI/Input/SoftKeyLayout.cs` ((8,4) entry), `docs/Keyboard/keyboard matrix.md`,
  `docs/Keyboard/keyboard mappins.md`, `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`
  (+3 tests, 75 total in `P2000.UI.Tests`, all green).
- **Synced:** superseded, not separately synced — the "accent aigu/grave" identification here
  was itself wrong and was corrected three entries below (2026-07-20, "Bracket keys are arrows")
  to **¼ (unshifted) / ¾ (shifted)**; that corrected fact is what's in
  `docs/P2000T-reference.md` §5f. The real-apostrophe-position detail ((0,6) shifted, i.e.
  Shift+7) is intentionally left out of the reference doc per its own stated policy of not
  duplicating the full key table — it's an ordinary digit-row shift pairing, not an independently
  surprising fact like the others in this section.

### 2026-07-20 — Milestone 3a follow-up: soft-keyboard icons + focus-return fix (owner feedback)
- **Icons added for pictorial keys.** `SoftKeyDef` gained optional `BaseIcon`/`ShiftedIcon`
  (file-name-only, resolved to `avares://P2000.UI/Assets/Icons/{name}.png`); `KeyboardWindow`'s
  `DataTemplate` shows an `Image` instead of the text legend when one is set, via the new
  `IconUriToBitmapConverter`. No new NuGet dependency — plain PNG via Avalonia's built-in
  `AssetLoader`/`Bitmap`, per root CLAUDE.md's minimal-dependency rule (an SVG library would
  have needed one). Wired to the `Assets\Icons\**` `AvaloniaResource` glob in the `.csproj`
  (previously absent — this project had no icon infrastructure at all before this).
- **Icons created (7, via a scratch Python/Pillow script — no image-authoring tool exists in
  this toolchain):** envelope (5,0 base), tape/WIS (6,3 shifted), disk/dsk (6,2 shifted), flash
  (7,2 shifted), flag/00 (2,2 shifted), plus the two keys beside the space bar — `home_up`
  ((0,2) base, combining the real key's ↖ + ↑ glyphs) and `end_down` ((2,5) base, combining
  ↘ + ↓) — scope was owner-specified as "all numpad keys, also the key to the left and right of
  the space bar." Textual numpad keys (INL/OPN/ZOEK/START/STOP/DEF/M and the plain digit/symbol
  pairs) were left as text — the real keycaps show colored TEXT there, not pictograms, so an
  icon would misrepresent them; only genuinely pictorial keys got images.
- **Focus-return fix (owner-reported):** clicking a soft key left the Keyboard window
  OS-focused, so the user couldn't type into the emulator without re-clicking it. Added
  `KeyboardWindowVm.KeyActivated` (raised at the end of every `ActivateAsync` branch — lock,
  sticky, and regular key); `KeyboardWindow.axaml.cs` subscribes and calls `Owner?.Activate()`.
  Verified live: soft-click "1" then a real host "2" keypress (no manual re-click) both landed
  in the emulator's BASIC prompt as "12".
- **Removed:** `StringNotEmptyConverter` (dead code once the shifted-label visibility logic
  moved to `SoftKeyVm.ShowShiftedText`, which also accounts for the icon case).
- **Tests:** existing 75 (`P2000.UI.Tests`) still green — icon rendering and the focus-return
  Activate() call are both AXAML/window-lifecycle behavior not covered by headless VM unit
  tests (same StorageProvider/TopLevel limitation already noted for ms.12/13's dialogs); verified
  live instead, per that established pattern.
- **Applies to:** `src/P2000.UI/Input/SoftKeyLayout.cs` (`BaseIcon`/`ShiftedIcon`, new icon refs),
  `src/P2000.UI/ViewModels/KeyboardWindowVm.cs` (icon computed properties, `KeyActivated` event),
  `src/P2000.UI/Views/KeyboardWindow.axaml(.cs)` (icon template, focus-return handler),
  `src/P2000.UI/Views/StatusConverters.cs` (`IconUriToBitmapConverter` added,
  `StringNotEmptyConverter` removed), `src/P2000.UI/P2000.UI.csproj` (`AvaloniaResource` glob),
  `src/P2000.UI/Assets/Icons/*.png` (new, 7 files).
- **Synced:** no (implementation-only UI polish; no new hardware content).

### 2026-07-20 — Real bug: Standard-Host forced-shift release hardcoded to Left Shift only
- **Owner-reported:** in Standard-Host mode, physical Shift+2 produced an unrelated glyph
  instead of `@`; Shift+3 similarly wrong instead of `#`.
- **Root cause:** `HostKeyTranslator`'s "force P2000 Shift off" path (needed for `@`, `#`, `;`,
  `<` — see the digit-row/Standard-Host table) always released the hardcoded P2000 Left-Shift
  crosspoint (9,0), regardless of which real host Shift key was actually held. Typing Shift+2
  with the **Right** Shift key (mapped to (9,7) — the natural choice when reaching for a
  left-side key with the right hand) meant the "force off" released a crosspoint that was never
  down — a no-op — while the REAL (9,7) press stayed asserted the whole time. So pressing (6,7)
  for `@` still read as shifted on the P2000 side, producing ¨ (the umlaut/diaeresis key) instead.
  Same mechanism for `#`/(2,4) and any other forced-off case.
- **Fix:** track which real Shift key(s) are actually down in a `_realShiftDown` set; "force
  off"/"restore" now release/re-press exactly those crosspoints (`ReleaseRealShifts`/
  `RestoreRealShifts`), never a hardcoded position. The "force ON" path (used when NO real Shift
  is held at all, e.g. plain `=` or `[`) is unaffected — there's nothing real to conflict with,
  so a synthetic (9,0) press/release is still safe there.
- **Not (yet) explained:** the owner also reported `[` showing two arrow characters instead of
  `[`. This isn't reachable from the same bug (the force-ON path for an unshifted key has no
  real-Shift conflict to get wrong) — most likely a keyboard-layout mismatch, where the physical
  key producing `[` on the owner's layout doesn't arrive as Avalonia's `Key.OemOpenBrackets`.
  Flagging rather than guessing; needs the owner to confirm which key/layout is in play.
- **Tests (+1, 76 total):** `StandardHost_RightShift2_ReleasesTheRightShiftCrosspoint_NotLeft` —
  reproduces the exact reported scenario with `Key.RightShift`, asserting (9,7) (not (9,0)) is
  what gets released and restored.
- **Applies to:** `src/P2000.UI/Input/HostKeyTranslator.cs` (`_realShiftDown`,
  `ReleaseRealShifts`/`RestoreRealShifts`, renamed `ShiftRow`/`ShiftCol` →
  `SyntheticShiftRow`/`SyntheticShiftCol`), `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`.
- **Synced:** no (implementation-only bug fix).

### 2026-07-20 — Real bug #2: force-off release and target press need a genuine field gap
- **Owner-reported (persisted after the left/right-crosspoint fix above):** Standard-Host
  Shift+2 still showed an up-arrow instead of `@`; Shift+3 still showed a block instead of `#`.
  Owner also confirmed via the **soft-keyboard mouse click** (not the physical keyboard) that
  the same wrong output occurs — ruling out a real-hardware/OS key-delivery quirk and ruling
  out the left/right-crosspoint fix as the (sole) cause, since the soft keyboard always drives
  a fixed `Key.LeftShift`, no ambiguity possible.
- **Diagnosed via a machine-level test** (bypassing `P2000.UI` entirely — booted the real
  `assets/BASIC.bin` cartridge, drove `KeyboardDevice.SetKey` directly with the exact sequence
  `HostKeyTranslator` emits, read the echoed byte back from VRAM): a clean, isolated, never-
  shifted press of (6,7) correctly echoes `@`. But releasing an ALREADY-held Shift crosspoint
  and pressing (6,7) in the same synchronous instant (same field) still echoes `^` — the same
  result as if Shift were genuinely still held. A one-field (20 ms) real gap between the
  release and the press was sufficient and necessary in every case tested (`gapFields: 0` →
  wrong, `1/2/3` → correct) — the monitor ROM's keyboard scan apparently needs to observe a
  moment with Shift genuinely released before it will register a subsequent keypress as
  unshifted, even though the emulated matrix state is technically already correct the instant
  both `SetKey` calls land.
- **Force-ON needs no such gap (also empirically confirmed):** the mirror case (no real Shift
  held at all, e.g. plain `=` or `[`) works correctly with ZERO gap between asserting the
  synthetic Shift and pressing the target key — there's no stale "already pressed" state to
  escape; it's exactly how a normal Shift+key combo already looks to the ROM.
- **Fix:** `HostKeyTranslator`'s force-OFF path now defers the target-key press via a
  fire-and-forget `Task.Delay(40ms)` after releasing the real Shift crosspoint(s), instead of
  emitting both in the same synchronous call. The target position is still recorded in
  `_activePress` immediately (so `KeyUp` knows what to release even if the press is still
  pending behind the gap). Force-ON is unchanged — confirmed not to need it.
- **Verified live (owner's build, this session):** Standard-Host Shift+2 → `@`, Shift+3 → `#`,
  both via soft-keyboard click, after the fix.
- **Tests:** `P2000.Machine.Tests/Boot/KeyboardScanTimingTests.cs` (new, permanent) — a
  machine-level regression pair proving the ROM genuinely needs the gap (`...NoGap_
  StillReadsAsShifted`) and that the gap fixes it (`...NeedARealFieldGap_ToRegisterAsUnshifted`),
  independent of `P2000.UI` — protects the underlying ROM-timing assumption itself, not just
  the translator's bookkeeping. `HostKeyTranslatorTests.cs` updated: the two existing force-off
  tests now `await` past the internal gap; added
  `StandardHost_ForceOff_TargetPressIsDeferred_NotImmediate` asserting the press genuinely
  hasn't landed right after `KeyDown` returns. 77/77 green in `P2000.UI.Tests`, 347/347 in
  `P2000.Machine.Tests`.
- **Not chased further (out of scope for this fix):** the same machine-level diagnostic
  surfaced what look like a couple of additional matrix-table transcription discrepancies
  (e.g. Shift+3's POSITIONAL target, port (0,4), read `#` rather than the previously-recorded
  `£` in one sweep) — but that sweep accumulated a long, un-cleared BASIC input line across
  ~140 key presses and is not trusted as clean evidence (probably input-buffer-length
  interference, same category of artifact as the timing bug this entry fixes). Flagging rather
  than silently editing the table on unverified data — if `£` is ever reported wrong live,
  re-verify with a fresh-boot single-key test the way (2,4)="#" and (6,7)="@" were confirmed
  above, not a long accumulated sweep.
- **Applies to:** `src/P2000.UI/Input/HostKeyTranslator.cs` (`ForceOffGapMilliseconds`,
  `PressAfterForceOffGapAsync`), `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`,
  `tests/P2000.Machine.Tests/Boot/KeyboardScanTimingTests.cs` (new).
- **Synced:** yes (already present in `docs/P2000T-reference.md` §5f's "Confirmed keyboard
  facts" — the "Keyboard scan timing quirk — CONFIRMED" bullet is exactly this ROM-timing fact;
  this entry's own "Synced: no" was stale bookkeeping, caught during the 2026-07-24
  sync-and-archive pass).

### 2026-07-20 — Bracket keys are arrows, not brackets; matrix table fully re-verified via ROM data
- **Owner-reported:** Standard-Host `[` still showed two arrows; `]` showed one right-pointing
  arrow. Also: P2000-Authentic `[`/`]` show `@` and a right arrow "instead of `]`" — i.e. the
  owner expected literal brackets from Authentic mode too.
- **Root cause — NOT a translator bug:** `Saa5050Font.cs` (already-shipped, sourced font table)
  reassigns ASCII 0x5B/0x5D/0x5E to Left-Arrow/Right-Arrow/Up-Arrow GLYPHS. (7,4) — the position
  ms.3's photo transcription labeled "] [" — genuinely CANNOT display a literal bracket on real
  hardware; it displays an arrow. **P2000-Authentic mode showing arrows for `[`/`]` is correct,
  faithful emulation** — not a bug to fix. Standard-Host mode, which promises the literal host
  character, has nothing to redirect `[`/`]` to, so both are now "no P2000 equivalent" (null),
  same category as `~`/`^`/`{`/`}`/`\`/`|`.
- **Owner-suggested investigation that unlocked everything else:** the monitor ROM does a
  port-matrix→keycode conversion; the BASIC cartridge separately does a keycode→ASCII
  conversion via a table at Z80 address 6164. Dumping that table from `assets/BASIC.bin`
  (`table[keycode] = asciiByte`, keycode 0–~140ish before the table runs into unrelated Z80
  opcodes) gave ROM-sourced ground truth for the whole keyboard instead of continuing to guess
  from the photo.
- **Found (keycode formula, confirmed against 7+ independently-known facts):** unshifted keycode
  = `row×8+col`; shifted keycode = that **+72**. Also found: **BASIC forces all letters to
  uppercase** regardless of the table's own (mixed-case) entries — explains why every earlier
  letter-row test showed no shift/case distinction.
- **Found (the +72 theory is NOT universally reliable — do not trust it alone):** it wrongly
  predicted Shift+3 would show `#` (keycode 76 → 0x5F). Direct machine-level testing AND the
  owner's live observation both confirm Shift+3 genuinely shows **`£`** (byte 0x23) — which is
  itself a `Saa5050Font.cs` remap (British Pound) that an earlier pass of this investigation
  initially misread as plain '#' by naively casting the byte to ASCII without checking the font
  table for 0x23 specifically (only 0x5B/5C/5D/5E/5F/60/7B/7D/7E/7F had been checked). **Lesson
  recorded for next time:** decode every VRAM byte against the FULL `Saa5050Font.cs` comment
  table, never assume plain ASCII, and never trust a keycode-arithmetic extrapolation over a
  direct machine-level test.
- **Corrections applied, each independently confirmed by a direct SetKey→VRAM test (not the
  table formula alone):**
  - `[` / `]` / `` ` `` (backtick): no P2000 equivalent in Standard-Host (arrows/fractions only
    exist at those positions — see the (8,4) note below for backtick specifically).
  - Numpad "+/x" (5,2) shifted is `*`, not the letter `x`.
  - Numpad "-/:" (5,3) is `-` unshifted / ÷ (divide) shifted, not `_`/`:` — pairs with (5,2) as
    a calculator-style arithmetic-operator row, not "minus/colon".
  - Numpad "9/M" (6,0) shifted produces no visible character (silent function).
  - Numpad "5" (8,2) shifted doesn't echo into the input line either, but DOES trigger some
    other screen-level redraw (looked like it touched the top banner row) — genuinely unclear
    what it does; flagged, not chased further.
  - (8,4) (accent aigu/grave key): now CONFIRMED (independently, twice) to render as ¼/¾, not
    any accent mark — upgraded from "open question" to a settled fact. Backtick's
    Standard-Host redirect (which assumed (8,4) shifted gives literal backtick) is also wrong
    for the same reason — now null, same as the brackets.
  - (2,4) unshifted (`#`) and (0,4) shifted (`£`): re-confirmed CORRECT as originally
    documented — the +72 theory briefly cast doubt on both, wrongly.
- **Tests:** `tests/P2000.Machine.Tests/Boot/MatrixCharacterOutputTests.cs` (new, permanent,
  14 tests) — direct SetKey→VRAM assertions for every position in this entry, both the
  regression guards (positions that were already correct) and the actual corrections, plus the
  two silent-function keys. `HostKeyTranslatorTests.cs`: replaced the now-stale backtick test
  with `StandardHost_Backtick_IsNoOp`; added `StandardHost_Brackets_AreNoOp` and
  `Authentic_Brackets_CorrectlyShowArrows_NotBugged` (the latter explicitly asserting Authentic
  mode's arrow behavior is intentional, not a regression to "fix" later). 79/79 green in
  `P2000.UI.Tests`, 361/361 in `P2000.Machine.Tests`.
- **Applies to:** `src/P2000.UI/Input/KeyMap.cs` (comments + `OemTilde`/`OemOpenBrackets`/
  `OemCloseBrackets` overrides all now null), `src/P2000.UI/Input/SoftKeyLayout.cs` (labels for
  (5,2)/(5,3)/(6,0)/(8,4), removed misleading Standard-Host legend on the bracket keys),
  `docs/Keyboard/keyboard matrix.md`, `docs/Keyboard/keyboard mappins.md`,
  `tests/P2000.Machine.Tests/Boot/MatrixCharacterOutputTests.cs` (new),
  `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`.
- **Synced:** yes (already present in `docs/P2000T-reference.md` — the SAA5050
  national-character-set remaps live in §5 SAA5050 behaviour ("Confirmed remaps found so
  far..."), and the letter-auto-uppercase + keycode-arithmetic-caution + (8,4) fraction-glyph
  facts are in §5f's "Confirmed keyboard facts." The full re-verified matrix table itself
  correctly stays out of the reference doc — it lives in `docs/Keyboard/keyboard matrix.md` per
  §5f's own stated policy against duplicating the full table. This entry's "Synced: no" was
  stale bookkeeping, caught during the 2026-07-24 sync-and-archive pass).

### 2026-07-19 — Real physical P2000T hardware test: matrix confirmed, three host-key wiring bugs found

- **Owner tested a real physical P2000T hooked to a monitor** and directly confirmed the (7,4)
  arrow behavior and the (8,4) ¼/¾ behavior from the prior findings entries match the emulator
  exactly — first ground-truth confirmation of this table against actual hardware rather than a
  photo transcription or ROM-table extrapolation.
- **Three NEW bugs found, but a different class from anything above:** the physical MATRIX
  values at (8,4)/(8,5)/(8,7) were already correct — the bug was which HOST KEY `KeyMap.cs`
  wired to which position:
  - Host `=`/`+` key (left of backspace) was wired to (8,5) `;`/`+`; should be (8,4) `¼`/`¾` —
    that's where it sits on a real keyboard.
  - Host `'`/`"` key (left of Enter) was wired to (8,4) `¼`/`¾`; should be (8,7) `:`/`*`.
  - Host `\`/`|` key did nothing (no host key reached this position at all); owner suggested
    giving it the P2000's `#`/block key function at (2,4).
- **Fix:** swapped the three `_map` entries (`OemQuotes`→(8,7), `OemSemicolon`→(8,5) filling the
  gap `OemPlus` vacated, `OemPlus`→(8,4)); added `Key.OemPipe`→(2,4) (confirmed via reflection
  dump of the Avalonia `Key` enum to be the correct, distinct member from `Key.OemBackslash`,
  which is already used at (3,2) for the ISO "<>" key). Updated `_standardHostOverrides`
  accordingly: removed the now-redundant `(OemSemicolon, false)` override (positional already
  gives `;`), added a new `(OemPlus, true)` override targeting `(8,5,true)` for `+` (OemPlus no
  longer naturally reaches it), and added `(OemPipe, false/true)` → null (no P2000 equivalent for
  a literal backslash/pipe character — OemPipe only reaches (2,4) in P2000-Authentic mode).
- **Tests:** `HostKeyTranslatorTests.cs` — updated the apostrophe/accent-key tests for the new
  positions, added `StandardHost_ShiftPlus_RedirectsAwayFromOemPlusOwnPosition`,
  `StandardHost_PlainSemicolon_NeedsNoOverride_PositionalAlreadyCorrect`,
  `StandardHost_ShiftSemicolon_RedirectsToColon`, `Authentic_PlainEqualsKey_...`,
  `Authentic_Backslash_SendsThePositionalBlockKey`, `StandardHost_Backslash_IsNoOp`.
  `SoftKeyLayoutTests.cs`'s `KeysWithNoHostKey_...` set shrank from 3 to 2 positions since (2,4)
  is no longer host-unreachable. 85/85 green in `P2000.UI.Tests`, 361/361 in
  `P2000.Machine.Tests`.
- **Applies to:** `src/P2000.UI/Input/KeyMap.cs` (`_map` + `_standardHostOverrides`),
  `src/P2000.UI/Input/SoftKeyLayout.cs` (swapped `HostKey`/`HostBase`/`HostShifted` on the three
  affected `SoftKeyDef`s, added `OemPipe` to the (2,4) entry), `docs/Keyboard/keyboard matrix.md`,
  `docs/Keyboard/keyboard mappins.md`, `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`,
  `tests/P2000.UI.Tests/Input/SoftKeyLayoutTests.cs`.
- **Synced:** yes (2026-07-24 — this entry's real-hardware corroboration doesn't add content
  beyond what §5f already has; the three bugs found here are host-KeyMap wiring, implementation-
  only, not new hardware facts).

### 2026-07-19 — Shift+numpad ZOEK bug (Windows nav-key override) + soft-keyboard ANSI reshaping

- **Owner-reported bug:** Shift + physical numpad-1 (NumLock on) didn't activate ZOEK, in either
  mode. **Root cause:** Windows overrides the reported key when Shift is held during a numpad
  press — with NumLock on, Shift+NumPad1 delivers `Key.End`, not `Key.NumPad1` (a documented OS
  behavior for text-selection convenience), indistinguishable from a real End-key press by `Key`
  alone. Avalonia's `KeyEventArgs.PhysicalKey` is scancode-based and unaffected by this override
  (confirmed via reflection dump: `PhysicalKey.NumPad0..9`/`NumPadDecimal` exist as distinct
  values from `PhysicalKey.End`/`Home`/arrows). **Fix:** `HostKeyTranslator.KeyDown`/`KeyUp` now
  take an optional `PhysicalKey` parameter and normalize the effective key through a
  `_physicalNumpadOverride` table before anything else runs — recovering `Key.NumPad1` (etc.)
  regardless of what Windows reported. `DisplayWindow.axaml.cs` passes `e.PhysicalKey` through;
  the soft-keyboard's synthetic presses are unaffected (they pass explicit `Key` values with no
  ambiguity, using the default `PhysicalKey.None`).
- **Tests:** `HostKeyTranslatorTests.cs` — `Authentic_ShiftNumpad1ReportedAsEnd_StillReachesZoek`,
  `StandardHost_ShiftNumpad1ReportedAsEnd_StillReachesZoek` (both simulate the exact Windows
  quirk: `KeyDown(Key.End, PhysicalKey.NumPad1)`), `Authentic_RealEndKey_UnaffectedByNumpadRecovery`
  (a genuine End press must NOT be swallowed), `Authentic_NumpadWithDefaultPhysicalKey_...`
  (soft-keyboard's no-PhysicalKey calls still work). Live end-to-end reproduction of the exact
  Windows quirk via computer-use automation was attempted but not completed this session — that
  input channel proved unreliable (see below) — so this fix is verified at the translator level
  only; owner should confirm on real hardware.
- **Owner-reported layout gap:** the soft-keyboard's shift row always showed the P2000T's own
  ISO-style shape (narrow left Shift + a "&lt;&gt;" key between Shift and Z, wired to
  `Key.OemBackslash`) even in Standard-Host mode, but the owner's real host keyboard is ANSI-shaped
  (wide left Shift, no key there) — asked whether Standard-Host mode should reshape to match
  (owner chose: reshape Standard-Host only, keep P2000-Authentic showing the P2000's real shape).
  **Fix:** `SoftKeyDef` gained `IsIsoOnly` (marks the "&lt;&gt;" key) and `StandardHostWidth`
  (overrides `Width` in Standard-Host mode only, set to 2.75 on the left Shift key — absorbing the
  hidden key's 1.0 width on top of Shift's own 1.75). `SoftKeyVm.IsVisible`/`PixelWidth` become
  mode-aware; `RefreshLabels()` renamed to `RefreshForModeChange()` since it now also refreshes
  shape, not just legends. `KeyboardWindow.axaml`'s button template binds the new `IsVisible`.
  Live-verified via screenshot in both modes: Standard-Host hides `<` and widens Shift; Authentic
  shows both unchanged.
- **Tests:** `KeyboardWindowVmTests.cs` — `Authentic_IsoKeyVisible_ShiftAtOwnWidth`,
  `StandardHost_IsoKeyHidden_ShiftWidened`, `StandardHost_OtherKeys_KeepTheirOwnWidth`. 92/92 green
  in `P2000.UI.Tests`.
- **Methodology note — computer-use as a live-testing channel proved unreliable this session:**
  a `key` tool call for a Shift+letter combo occasionally auto-repeated dozens of times instead of
  a clean press+release (flooding the input line), and a transient Windows shell overlay
  ("ShellHost", likely a notification toast) intermittently stole foreground focus and blocked
  all clicks/keys for several tool calls with no fix available from this side. Pixel-reading the
  soft-keyboard's tiny labels and the Debugger's VRAM hex view was also too unreliable at the
  window's native size to trust (aspect-distorted zoom crops, easy to miscount rows). The
  Debugger's **Memory Watch** (exact address → hex+ASCII text, e.g. VRAM row 7 col 0 = `0x5000 +
  7*80 = 0x5230`) was the one live-verification technique that gave an unambiguous, address-precise
  answer (confirmed Standard-Host `=` writes `0x3D` correctly) — prefer it over pixel-counting for
  any future live VRAM check.
- **Applies to:** `src/P2000.UI/Input/HostKeyTranslator.cs`, `src/P2000.UI/Views/DisplayWindow.axaml.cs`,
  `src/P2000.UI/Input/SoftKeyLayout.cs`, `src/P2000.UI/ViewModels/KeyboardWindowVm.cs`,
  `src/P2000.UI/Views/KeyboardWindow.axaml`, `tests/P2000.UI.Tests/Input/HostKeyTranslatorTests.cs`,
  `tests/P2000.UI.Tests/ViewModels/KeyboardWindowVmTests.cs`.
- **Synced:** no, and correctly so (2026-07-24 review) — the Windows Shift+NumLock nav-key
  override is host-OS behavior, not a P2000 hardware fact; out of scope for
  `docs/P2000T-reference.md`, which documents the emulated machine, not the host environment.
  No further sync action needed; implementation-only.

### 2026-07-19 — (5,0) "envelope/centre-tab" mislabel: envelope is shifted, not base; real function found

- **Owner-reported (real P2000T hardware):** the numpad key ms.3 coded as unshifted-envelope
  ("centre-tab" raw transcription, envelope shifted per the doc note) actually has the envelope
  as its **shifted** function, and it performs **clear screen** (the rectangle-with-cross icon
  visually gets "x-ed out"). The **unshifted** function is a different glyph — a vertical bar /
  right-arrow / left-arrow / vertical bar ("|→←|") — and performs **clear line + home cursor** to
  the leftmost column of the current line. "centre-tab" was a misread of this unshifted glyph.
- **Fix:** `SoftKeyLayout.cs`'s (5,0) entry now sets `BaseIcon: "clear_line"` (new asset,
  generated to match the existing icon set's style — light line-art on transparent 32×32) and
  `ShiftedIcon: "envelope"` (swapped from the other way around); `Base`/`Shifted` text fallbacks
  updated to "CLR"/"CLS". Both icons render stacked on the one key face (matching how a real
  keycap prints both functions at once), confirmed via a live screenshot.
- **Applies to:** `src/P2000.UI/Input/SoftKeyLayout.cs`, `src/P2000.UI/Assets/Icons/clear_line.png`
  (new), `docs/Keyboard/keyboard matrix.md`, `docs/Keyboard/keyboard mappins.md`. No test changes
  needed (this position has no host key at all — `HostKey: null` — so nothing in
  `HostKeyTranslator`/`KeyMap` is affected; existing `SoftKeyLayoutTests.cs` coverage for
  "positions with no host key" still holds).
- **Synced:** yes (2026-07-24 sync pass — added to `docs/P2000T-reference.md` §5f's "Confirmed
  keyboard facts": "(5,0) — CONFIRMED function pair... unshifted = clear line + home cursor...
