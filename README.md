# ComboMod

A rebalancing framework and in-game editor for **Combolands: Roguelike Citybuilder**
(Steam appid 4075620, Unity 6000.0.66f2, Mono).

Change any base stat on any building or item — from a text file, or live while you play —
without ever putting your save at risk.

---

## Why it is safe

The run save stores per-piece **deltas only** (`LocalRangeChanges`, `LocalMultChanges`, `Count`,
`StoredValue`). No base stat is serialized anywhere; base values are re-read from code on every
load. So a rebalance is invisible to the save format in both directions:

- A save written with ComboMod loaded opens fine in an unmodded client.
- Removing ComboMod restores vanilla numbers with no migration step.

The one thing that would break this is introducing a new `GameTag`, because `JsonUtility` writes
enums as bare integers an unmodded client cannot resolve. **Both** entry points reject that —
`ComboModApi.Tune` and the pack parser — so the guarantee is mechanical rather than a matter of
discipline.

Not everything in the mod is in that category. See [Two tiers](#two-tiers) below.

[docs/modding-surface.html](docs/modding-surface.html) is the full audit: every knob with its
measured vanilla range, the clamps that actually constrain you, and where the save format stops
forgiving.

---

## Install

1. [BepInEx 5.4.23.3 (x64, Unity Mono)](https://github.com/BepInEx/BepInEx/releases) into the
   game folder.
2. `ComboMod.Core.dll` into `BepInEx/plugins/ComboMod/`.
3. Launch, press **F6**.

**Uninstall:** delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version` and `BepInEx/`.
Steam's file integrity check stays green — nothing in the game install is modified.

---

## Balance packs

A pack is a plain text file. No compiler, no DLL — this is how tuning is meant to be shared.

Put them in `BepInEx/config/ComboMod/packs/`. That is under `config` rather than beside the
plugin so that updating ComboMod never deletes your tuning.

```ini
# Faster bakeries
[pack]
name = Faster Bakeries
author = you
version = 1.0
description = Bakeries fire faster and pay more.

[building.Bakery]
Cooldown = 3
Money = 5

[item.Clover]
Multiplier = 2
```

- Sections are `[pack]`, `[building.Tag]` or `[item.Tag]`, using the game's own names — browse
  them in the panel.
- Stat names are exactly what the panel shows (see [What is editable](#what-is-editable)).
- `#` and `;` start comments.
- **A bad line costs one line, not the pack.** Unknown buildings, unknown stats, unparseable
  values and malformed syntax are each skipped with a warning naming the line number, shown in
  the panel rather than buried in a log.

Tune in game, then **Save as pack** to write your changes out. **Reload packs** picks up edits
made in a text editor without restarting.

### Why not JSON

Packs exist so that someone who does not write code can author and share one, and INI is
hand-writable and diff-friendly. Unity's `JsonUtility` cannot round-trip dictionaries anyway, so
JSON would have meant taking on a serializer dependency to read twenty key/value pairs.

---

## The in-game editor

**F6** opens it. **F7** flips every tune on and off at once, for comparing against vanilla
without a restart. Both rebindable in the config.

### Registered tunes

Everything currently registered, grouped by source, showing the real before/after of each field.
Per-row switches re-apply immediately.

### Browse & edit

Every **Building** (167), **Item**, and **Consumable** the game has, searchable. Pick one and
edit any base stat in place — type a number and it applies on the keystroke. Edited stats show
in amber, edited pieces are marked `*`, `x` drops one edit, `Clear edits` drops all.

**Give** hands the selected thing to the player:

| Selected | Becomes |
|---|---|
| Item / heirloom | an heirloom (gems auto-route to the Jewellery Box, tomes to the Bookshelf) |
| Consumable / spell / favour | a consumable |
| Building | a **blueprint card**, stacking onto an existing one |

The button disables itself with a reason when it cannot work. Consumables carry their data on
`ConsumableData` rather than a behaviour object, so that mode is give-only — there are no base
stats to edit.

### Run

Live values for the current run — see [Two tiers](#two-tiers) for which persist.

Money, weeks remaining, score, milestone target, rerolls/removes/dismisses/rewinds, and
inventory slot counts. Slots are **add-only**: the game has no `RemoveSlot`, so a count rises and
never falls.

### Packs

Loaded packs with per-pack switches, author, version, change count, and any parse warnings.
Reload, and open the packs folder.

### Scale

IMGUI draws at a fixed pixel size, so the default is tiny on a high-resolution display. The
`−` / `+` / `Reset` row scales every font and dimension together — scaling `GUI.matrix` instead
would stretch the glyph bitmaps and just make them blurry. Defaults to `Screen.height / 1080`,
resolved on first paint (during plugin load the game window is not sized yet and `Screen.height`
reports a placeholder).

---

## Two tiers

**Tier 1 — free.** Nothing here touches a save. Every base stat on every building and item, all
of it reversible by removing the mod. This is what packs and Browse & edit change.

**Tier 2 — persists.** These are in `SerializedGameState`. Plain integers and vanilla tags, so an
unmodded client still reads the save and nothing is corrupted, but the change is **permanent for
that run**:

| | |
|---|---|
| Money | `MoneyCount` |
| Rerolls / Removes / Dismisses / Rewinds | |
| Heirloom + consumable slot counts | add-only |
| Given items | stored by `GameTag` |

Runtime only, reset on reload: **weeks remaining, score, milestone target**.

Modded runs cannot unlock Steam achievements — ComboMod swaps `AchievementsHandler`'s
`IAchievementPlatform` for a no-op by default. This does **not** stop `Unlocks.save` recording
victories and lifetime counters; a modded win still counts locally.

---

## Writing a code mod

Packs cover balance. Use the API when you need logic.

```csharp
[BepInPlugin("your.guid", "Your Mod", "1.0.0")]
[BepInDependency(ComboMod.Plugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
public sealed class YourMod : BaseUnityPlugin
{
    private void Awake()
    {
        ComboModApi.Tune(GameTag.Bakery, t =>
        {
            t.Cooldown = 3;             // vanilla 5
            t.Money = 5;                // vanilla 0
        });

        ComboModApi.Tune(GameTag.Blacksmith, t =>
        {
            t.Rarity = Rarity.Common;   // vanilla Rare: roll weight 0.05 -> 0.70
        });
    }
}
```

Tunes are *registered*, not applied immediately. The game discards and rebuilds its behaviour
dictionaries on every scene load, so a one-shot write at startup would appear to work and then
silently stop. ComboMod re-applies on each rebuild via a Harmony postfix.

### Precedence

When several sources touch the same stat, later wins:

**code → pack → live edit**

A hand-authored pack is more specific intent than a shipped DLL's defaults; something typed into
the panel just now is more specific still. Registration order decides within a layer. This is
explicit rather than falling out of plugin load order, which had the DLL silently overriding the
user's own pack.

---

## What is editable

The 17 numeric knobs plus `Rarity`:

`Cooldown` `Range` `BuyPrice` `ActivationCount` `ActivationChance` `MultParam` `Multiplier`
`Money` `Score` `Rerolls` `Removes` `Dismisses` `Enchant` `StoredValue` `RangeModification`
`CooldownModification` `RollChanceMultiplier` `Rarity`

Collection-valued knobs (`MinorCategories`, `ValidTriggers`) and `MajorCategory` are API-only —
not meaningfully editable as text.

**Nothing clamps you to vanilla ranges, because the game does not either.** The only hard clamp
in the stat system is cooldown flooring at 1. Measured vanilla ranges for all of them are in the
[audit](docs/modding-surface.html).

Two traps worth knowing:

- **Never set `RollChanceMultiplier` to exactly 0.** `ChooseBag.RemoveElement` only removes
  entries whose weight is `> 0`, so a 0-weight entry can be chosen but never removed and the
  draft silently offers *fewer* options. Use `ComboModApi.SuppressionWeight`.
- **Large `Range` costs rebuild time**, not query time — `BuildingController` keeps a
  precomputed tile index. Past ~12 it is gameplay-meaningless before it is a performance problem.

---

## Building

Requires the .NET SDK.

```bash
dotnet build src/ComboMod.Core/ComboMod.Core.csproj -c Release
```

`NuGet.config` adds the BepInEx feed (`BepInEx.Core` is not on nuget.org). Game assemblies are
referenced straight out of the Steam install with `Private=false`, so **nothing from the game is
ever redistributed**. Set `GameManaged` in the csproj if your install is elsewhere.

Target framework is **netstandard2.1** — the game's assemblies require it and 2.0 fails to link.

---

## Version gate

ComboMod reflects on private field names, which are not API. `SafetyGate` hashes
`Assembly-CSharp.dll` and compares against the audited build, warning on mismatch. Set
`RefuseOnVersionMismatch` to refuse instead.

A mismatch is usually harmless: when Combolands patched from build 24930533 to 24951781 during
development, re-auditing found every reflected field, both patched methods, and all balance data
unchanged. Any field that genuinely moves is reported **by name** at tune time.

Set the BepInEx log level to `Debug` for per-pass diagnostics — which behaviour instance was
touched and whether vanilla was restored first. Useful because the game re-initialises behaviours
from three separate call sites (`BehavioursController`, `ItemPool`, `BuildingCategoryVisualization`),
so apply passes are more frequent than you would expect.

---

## Scope

Rebalancing existing content, editing run state, and giving existing items. **Adding new
buildings or items is out of scope** — that needs new `GameTag` values, which means a stable id
allocator, postfixes on ~13 `Enum.GetValues(typeof(GameTag))` call sites, sidecar save manifests,
and a load-failure guard around `IOManager.LoadFile` (which currently has no try/catch at all).

The game ships its own dev cheat menu, unrelated to this mod: **RightShift+C+L** arms it, then
RightShift + **G** (money), **W** (weeks), **E** (end milestone), **B**/**I**/**C** (spawn
pickers), **PageUp** (fast scoring). The spawner is better than anything ComboMod would rebuild.
