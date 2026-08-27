# Changelog

## 0.2.0 — unreleased

Split into two plugins so a balance pack does not force a cheat panel on anyone.

### Added
- **Difficulty curve**: a `[milestones]` pack section setting what each milestone
  demands, per city size, with a `scale` multiplier for the whole curve. Sets all
  three rank thresholds unless one is named.
- **Placement rules**: `PlaceOn` inside a building's section sets which tile types
  it can be built on.
- **Fast map load**: the game rebuilds its whole spatial index once per building
  placed, then rebuilds it again at the end of the load anyway. On a full board
  that was 641 discarded rebuilds totalling ~33 seconds. Collapsed to one.
  Load went from 33 seconds to instant.
- **Profiler** (`Performance.Profile`, off by default): per-method call counts,
  totals and worst-case timings, frame statistics, and GC correlation. Built
  after two rounds of plausible-sounding reasoning produced two wrong answers.
- **Global economy**: an `[economy]` pack section for draft weights, rarity drift,
  shop composition, blueprint price and sell ratio. Makes Legendary draftable —
  vanilla rolls it at 0.00 in both tables, so those buildings never appear.
- **Manage tab**: see and clear what you are holding, and remove slots. The game
  has no `RemoveSlot`, and its panels break past a handful of slots, so ComboMod
  had to be able to undo what it let you add.
- **Balance packs**: shareable plain-text `.pack` files in
  `BepInEx/config/ComboMod/packs/`. No compiler needed. A malformed line is
  skipped with its line number rather than failing the pack.
- **In-game editor** (`ComboMod.Editor`): browse every building, item and
  consumable; edit any base stat live; give items; edit run values; manage packs.
- **Give**: heirlooms, consumables, and buildings-as-blueprints.
- **Run tab**: money, weeks, score, milestone target, consumable counters, and
  inventory slot counts.
- **UI scaling**, defaulting to screen height, resolved on first paint.
- **Integrity check**: on an unrecognised build, verifies every reflected member
  still exists and reports which are missing — a far more useful answer than a
  hash mismatch alone.

### Changed
- **Harmony patching is failure-isolated**: each group is applied in a guard, so
  a bad patch target costs that feature rather than silently disabling
  everything after it in `Awake`. Found the hard way — an ambiguous overload in
  one diagnostic probe took the whole plugin down with no error logged.
- **Precedence is now explicit**: code → pack → live edit, later wins. It
  previously fell out of plugin load order, which let a shipped DLL silently
  override the user's own pack.
- Per-field degradation for packs and live edits: a renamed game field costs one
  stat, not every stat on that piece.
- `ComboMod.Core` is headless — no UI references at all.

### Fixed
- Tunes applied twice per scene load, with the second pass recording the
  already-tuned value as the restore baseline. That silently destroyed the
  vanilla values `RevertAll` depends on.
- Milestone target only moved the win check, not the number the HUD prints — you
  could beat a milestone at a score the goal text said was insufficient.
- Weeks and target edits did not refresh the HUD, which only rebuilds at
  milestone start and week end, so correct values looked stuck.
- Auto UI scale read `Screen.height` during chainloader startup, before the game
  window is sized, and got a placeholder.
- Panel could be dragged off-screen and clipped past the window edge.

## 0.1.0

Initial framework: reflection-based tuning of the 18 base stats, Harmony
re-application on scene load, save-safety enforced by rejecting non-vanilla
tags, achievement suppression, and an assembly hash version gate.
