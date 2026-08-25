# Milestone spec — video control register + config-window relayout

> **STATUS: BOTH PARTS IMPLEMENTED 2026-08-04** — Part A as **machine milestone 26**, Part B as
> **UI milestone 21** (numbers taken from the findings logs, as this spec instructed, and both
> differed from what the reference doc would have given). Machine regression gate: **707 total /
> 695 passed / 12 skipped / 0 failed** against the 678/666/12 baseline, no pre-existing timing or
> contention result moved. UI: **253/253**, identical to baseline — then **259/259** after a
> separate bug this work uncovered. As-built records are in `P2000T-reference.md` §5g (register,
> blanking, the cartridge-specific pan write-back), §4 (the clamp and demoted modulo), §3a
> (config-window layout, `.state` v10, the config-drift reflection guard) and §5 (port `0x70`'s
> second, M-only user).
>
> **Two things this spec did not anticipate, worth recording:** (1) The spec's §A6 "check whether
> anything already claims `0x30`-`0x3F`" turned up nothing — but the same ROM enumeration found
> the **monitor ROM writes port `0x70`** on every cassette transfer, an M-only video-lockout
> mechanism that merely *looks* like it collides with the 80-column board. It does not.
> (2) Part B's owner-facing manual check surfaced a **milestone-20 regression** — `EnsureRamSeed`
> had been silently dropping the 80-column board from every Apply, so the axis had never actually
> worked. Unrelated to this spec, found because someone finally looked at the window.

**Two independent parts. Disjoint files, disjoint tests, either can land alone.**

| Part | Milestone | Scope |
|---|---|---|
| **A** | **machine milestone 26** | Output ports `0x30`–`0x3F`: horizontal pan (bits 0–6) + video blank (bit 7) |
| **B** | **UI milestone 21** | Config window two-column relayout (interim, ahead of an eventual tabbed view) |

> **Confirm the numbers against the findings logs, not the reference doc.** `CLAUDE_machine.md`
> §13's highest is 25 and `CLAUDE_ui.md` §14's is 20 as of 2026-08-04 — but the reference doc lagged
> by two UI milestones last time and produced a wrong number. Take the next free one from the logs.

**Sources:** `P2000T-reference.md` **§5g** (the new video-control-register entry — the whole of
Part A traces to it), **§4** (fetch address and the now-superseded modulo), **§5** 80-column mode
(the pan-hold interaction), **§3a** Config window (the layout decision for Part B).

---

# Part A — machine milestone 26: output ports `0x30`–`0x3F`

## A1. Why this exists

`Video.PanX` has been a plain settable property since machine milestone 5, with a doc comment
saying its CPU-facing control was unconfirmed. **Nothing has ever registered the port.** No
software can pan or blank in the emulator today. The 80-column milestone surfaced the gap; the
owner then supplied the Philips manual's description, which closes it and adds a feature the
project did not know about — **bit 7, video blanking**.

## A2. The register (from §5g)

| Bits | Function |
|---|---|
| **7** | Blank video to black when set. Does **not** destroy video memory contents. |
| **6–0** | Horizontal pan. `0` = leftmost, `40` = rightmost. Above 40 undefined → **clamp to 40**. |

- **A 16-port range, `0x30`–`0x3F`** — partial address decode, only the high nibble significant.
  A write to `0x3A` must behave **identically** to a write to `0x30`. Register the whole range;
  do not claim a single port.
- **Write-only.** No read-back of pan or blank. Do **not** answer reads on this range from a
  shadow byte — leave the read side unclaimed so it reads open-bus `0xFF` by the usual
  convention. (The manual does not state the read behaviour; open bus is the convention, and if
  software is ever seen reading the range, that is a finding worth reporting.)

## A3. Pan — the clamp, and the modulo it kills

```
PanX = min(value & 0x7F, 40)
```

**This replaces the current wrap.** §4's fetch address is
`videoBase + charRow * 80 + (PanX + column) % 80`; the `% 80` existed only to contain unclamped
pan values. With `PanX ∈ 0..40` and `column ∈ 0..39`, `PanX + column` maxes at **79** — exactly
the last byte of an 80-wide row — so **the modulo is unreachable**.

- Drop it, or demote it to a defensive assertion. State which you did and why.
- **Before changing it, check whether any existing test depends on wrap behaviour** — i.e. sets
  a pan above 40 and asserts the wrapped result. If one does, it is asserting the old placeholder,
  not hardware: update it to the clamp and say so in the findings log. Do not preserve a wrap
  test by special-casing.
- Clamp semantics: **clamp, not mask and not modulo.** 41→40, 100→40, 127→40. This is a
  deliberately chosen placeholder for genuinely undefined hardware behaviour (§5g), so mark it in
  code as such — the owner intends to test a real machine, and whatever they find replaces this.

## A4. Blank (bit 7)

New state on the video device: `VideoBlanked`. When set, the display renders **black**.

- **It does not touch VRAM.** Unblanking restores exactly the picture that would have been
  showing — including anything the CPU wrote while blanked. Blanking is a display-stage effect,
  not a buffer operation.
- **OPEN, but far less important than it first looks — does blanking stop the VRAM fetches?**
  The manual does not say. **This was initially written up as contention-relevant, with a
  proposed timing test; the owner corrected both.** On the P2000T the Z80 has **unconditional
  priority and never waits** (§4) — the SAA5020 is what gets denied. CPU timing is therefore
  already independent of video fetches, so blanking cannot be a speed trick and **no timing
  measurement can distinguish the two models.** Do not reach for the Spectrum-style
  "contention costs the CPU cycles" intuition; it does not apply to this machine.
  - **Consequence: there is no software-visible difference on real hardware either.** While
    blanked the output is black, so a corrupted fetch and a suppressed fetch look identical; and
    corruption is non-persistent (§4), so nothing survives into the next unblanked field. Only a
    logic analyzer can answer it.
  - **In the emulator the only thing it changes is the corrupted-cell overlay** — whether "show
    contention glitches" lights up cells during a blanked field. Emulated video output and
    emulated timing are identical either way. **Treat it as a display-diagnostic detail, not a
    fidelity question.**
  - **Build-against-now default: FETCHES CONTINUE, contention model untouched.** Gate the
    *output*, not the fetch. Mark the decision point in code so the alternative is one obvious
    change away.
- **What exactly goes black — decide and document.** The renderer produces a 928×626 full-field
  buffer with the 640×480 active picture at (144, 98), and blanking margins that are already
  flat black (§4a). Recommended: **blank the active window only**, leaving the border exactly as
  it renders today, since the bit describes blanking *video* and the borders carry no video. If
  you find a reason to prefer blanking the whole field, report it rather than choosing silently —
  it is visible in Full-Field display mode.
- **Interaction with 80-column mode:** the pan field is held cleared in hardware while in
  80-column mode, **but bit 7 is not** — they share a register, not a fate. A write of
  `0x80 | 25` while in 80-column mode must **blank the display and leave `PanX` at 0**. This is
  the one place the two milestones touch; test it explicitly.

## A5. Reset and state

- **Reset** (cold and warm): pan 0, unblanked. A blanked machine must not survive a reset.
- **Save-state:** both the pan value and the blank flag serialise. Bump `.state` per the standing
  discipline (v9 → v10, `MinVersion` likewise) — this adds device state to the stream.
  - `PanX` may already be serialised as part of the video device; if so, only the blank flag is
    new, but the bump is still required.
- `.cfg` is **not** affected — this is machine state, not topology.

## A6. Tests (Part A)

**Ports and decode**

- A write to each of `0x30`–`0x3F` has the identical effect; assert on at least `0x30`, `0x37`,
  `0x3F`, not just the base.
- Reads on the range return open-bus `0xFF`, not a shadow of the last write.
- End-to-end through real Z80 code (`OUT (0x30),A`), matching how the 80-column ports were tested.

**Pan**

- 0 → leftmost; 40 → rightmost; the fetch address for column 0 at pan 40 is `videoBase +
  charRow*80 + 40`.
- **Clamp:** 41, 100 and 127 all behave exactly as 40.
- Bit 7 set alongside a pan value does not corrupt the pan (`0x80 | 20` → pan 20, blanked).
- Panning changes what is displayed **without** changing VRAM contents.

**Blank**

- Bit 7 set → display renders black; VRAM unchanged; unblanking restores the same picture.
- CPU writes to VRAM while blanked are visible immediately on unblank.
- **Contention is identical blanked and unblanked** — pins the build-against-now default. Note
  what this test is and isn't: it does **not** distinguish real-hardware behaviour (nothing in
  software can, see A4), it just makes the emulator's chosen model explicit and named, so the
  alternative is a one-test change if a logic-analyzer capture ever settles it.
- **CPU timing is identical blanked and unblanked** — trivially true given the Z80's unconditional
  priority, but worth pinning precisely because it is the assumption that makes every "blanking
  as a speed trick" idea wrong. A future change that ever makes this test fail has broken the
  priority model.

**80-column interaction**

- In 80-column mode: a write with bit 7 set blanks; `PanX` stays 0.
- In 80-column mode: a write with a non-zero pan field leaves `PanX` at 0 (already covered by
  milestone 25's property-level test, now exercised through the real port).

**Reset and state**

- Cold and warm reset both clear pan to 0 and unblank.
- `.state` round-trip preserves pan and blank; version bumped; old version rejected.

**Regression gate:** the full `P2000.Machine.Tests` suite passes. Baseline is 678 total / 666
passed / 12 skipped. Nothing about pan or blank should move an existing contention or timing
result — if one moves, stop and report.

---

# Part B — UI milestone 21: config window two-column relayout

## B1. Why

The window has grown too tall as config axes accumulated; the 80-column Modifications section
tipped it over. **This is an explicitly interim layout.** The owner's stated end state is a
**tabbed** config window — see §3a. Do not build tabs in this milestone, and do not treat the
two-column form as a considered final layout.

## B2. The layout

| | |
|---|---|
| **Left column** | Model, RAM, Monitor-ROM path, Drives |
| **Right column** | Cassette, SLOT1, 80-column / Modifications |
| **Spanning both, underneath** | Load, Save, Apply, "Always start with this configuration" |

- **Presentation only.** No ViewModel restructuring, no changes to `BuildConfig`/`Apply`, no
  binding changes, no new properties. Every existing binding, command and enablement gate keeps
  working untouched — if this milestone changes anything in `ConfigWindowVm`, it has overreached.
- Keep the existing per-section grouping and headers; move the sections, don't redesign them.
- The action row spans the full width so Load/Save/Apply stay visually global rather than looking
  like they belong to the right-hand column.
- Sanity-check the window at its **narrowest sensible size** — two columns of controls plus
  file-path text boxes can force an awkward minimum width. If the result is worse than the tall
  single column, **stop and report** rather than shipping a layout that trades height for a
  horizontal scrollbar.

## B3. Tests (Part B)

Layout changes are largely not unit-testable, and inventing brittle visual-tree assertions is
worse than not testing. Reasonable coverage:

- The existing `ConfigWindowVmTests` / `StartupConfigurationTests` continue to pass unchanged —
  which is the actual assurance that the relayout was presentation-only.
- If there is an existing pattern for asserting a named control's presence in the visual tree,
  follow it for the moved sections; if there is not, **do not invent one for this**.
- Full `P2000.UI.Tests` green (baseline 253/253).
- **Manual check, for the owner:** open the config window, confirm every section is reachable
  without scrolling at the default window size, and that Apply / Load / Save / "Always start
  with" all still behave.

---

## What to report rather than decide

1. **Any existing test that depends on the pan wrap** (A3) — it is asserting a placeholder; say so
   rather than preserving it.
2. **Blanking the whole field vs the active window only** (A4), if you find a reason to prefer the
   former.
3. **Anything suggesting blanking does suppress fetches** (A4) — that would be a genuine hardware
   finding and changes the contention model.
4. **A two-column layout that forces an awkward minimum width** (B2) — report rather than ship.
5. **Any need to touch `ConfigWindowVm`** for Part B (B2) — that is the signal the change stopped
   being presentation-only.