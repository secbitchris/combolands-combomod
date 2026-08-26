# ComboMod

A save-safe rebalancing framework for **Combolands: Roguelike Citybuilder** (Steam appid 4075620,
build 24930533).

Everything reachable through this framework is safe by construction: it can change any base stat
on any existing building or item, and it cannot corrupt a save, because base stats are never
serialized. Delete the plugin and the game is vanilla again — including any save written while it
was loaded.

## Why it is safe

The run save (`GameState.save`) stores per-piece *deltas* only — `LocalRangeChanges`,
`LocalMultChanges`, `Count`, `StoredValue` — never base values. Base stats are read fresh from
code on every load. So a rebalance is invisible to the save format in both directions:

- A save written with ComboMod loaded opens fine in an unmodded client.
- Removing ComboMod restores vanilla numbers with no migration step.

The one thing that *would* break this is introducing a new `GameTag`, because `JsonUtility` writes
enums as bare integers that an unmodded client cannot resolve. `ComboModApi.Tune` rejects any tag
that is not a member of the vanilla enum, which is what keeps the guarantee mechanical rather than
a matter of discipline.

See [docs/modding-surface.html](docs/modding-surface.html) for the full audit: every knob, its
measured vanilla range, and where the real limits are.

## Usage

```csharp
[BepInPlugin("your.guid", "Your Mod", "1.0.0")]
[BepInDependency(ComboMod.Plugin.PluginGuid, BepInDependency.DependencyFlags.HardDependency)]
public sealed class YourMod : BaseUnityPlugin
{
    private void Awake()
    {
        ComboModApi.Tune(GameTag.Bakery, t =>
        {
            t.Cooldown = 3;         // vanilla 5
            t.Money = 5;            // vanilla 0
        });

        ComboModApi.Tune(GameTag.Blacksmith, t =>
        {
            t.Rarity = Rarity.Common;   // vanilla Rare: roll weight 0.05 -> 0.70
        });
    }
}
```

Tunes are *registered*, not applied immediately. The game discards and rebuilds its behaviour
dictionaries on every scene load (`BehavioursController.Awake`), so a one-shot write at startup
would appear to work and then silently stop. ComboMod re-applies on each rebuild via a Harmony
postfix.

## The in-game editor

Press **F6**. Two tabs:

**Registered tunes** — everything mods registered, grouped by mod, showing the real before/after
of each field. Each row has a switch; flipping one re-applies immediately, so the effect is
visible on buildings already placed. `Edit` jumps to that building in the browser.

**Browse & edit** — every building (167) and item the game has a behaviour for, searchable. Pick
one and edit any of its 18 base stats in place: type a number, it applies on the keystroke. The
`items` checkbox switches the list to heirlooms. Rarity cycles on click rather than being typed.

Edited stats show in amber, edited buildings are marked `*` in the list, and `x` next to a field
drops that one edit. `Clear edits` drops them all.

**Export C#** writes your live edits to `BepInEx/config/ComboMod.exported.cs` as compilable
`ComboModApi.Tune(...)` calls, so a session of tweaking becomes a real mod without retyping
numbers.

**F7** flips every tune on or off at once — the fast way to compare against vanilla without
restarting.

Live edits are ordinary registrations tagged `Live edits` that sort last, so they always win over
a mod's value and participate in enable/disable and restore like anything else rather than being
a parallel mechanism with its own bugs.

Both keys are rebindable in the config. The equivalent API is `ComboModApi.SetOverride()`,
`ClearOverride()`, `ClearAllOverrides()`, `RevertAll()`, `EnableAll()`, and per-registration
`Enabled` plus `Reapply()`.

## The Run tab

Live values for the current run, split by whether they survive a reload.

**Saved into `GameState.save`** — editing these changes your save; removing ComboMod will not
undo it. They are plain integers and vanilla tags, so an unmodded client still loads the save and
nothing is corrupted.

| Value | Notes |
|---|---|
| Money | written to the backing field, deliberately bypassing `ChangeMoney` so it does not inflate the lifetime gold counters in the permanent `Unlocks.save` |
| Rerolls / Removes / Dismisses / Rewinds | routed through the game's own `Change*` methods, triggers suppressed |
| Heirloom slots | starts at 6, **add-only** |
| Consumable slots | starts at 3, **add-only** |

**Runtime only** — reset on reload: weeks remaining, current score, milestone target.
`SerializedGameState` stores `MilestoneIndex` and per-milestone completed scores, but never the
week counters, the live score, or the target.

### Two traps this tab exists to handle

**The milestone target is two numbers.** `GameController.ScoreRequired` is what the win check
compares; `MilestoneManager.CurrentRequiredScore` reads the milestone ScriptableObject and is
what `ScorePanel` prints. Setting only the first gives you a real target the HUD never shows.
`SetScoreRequired` moves both, including all three of the milestone's rank thresholds.

**The HUD caches.** `UpdateGoalText` is only called at milestone start and week end, so a
mid-week edit to weeks or target is correct in memory but invisible until the next tick. Every
setter now refreshes what it owns — `UpdateGoalText` for weeks and target, `UpdateMoneyCount` for
money, `ResetCaches` for base stats.

### Slots are add-only

There is no `RemoveSlot` anywhere in the game, so a slot count rises and never falls for the rest
of the run. The familiar 10-heirloom ceiling is not enforced by `AddSlot` — it lives in the
callers, which stop offering the `SpellGainHeirloomSlot` potion at 10. Going past it works;
the panel layout beyond that is untested and ComboMod logs a warning.

### Adding items

Use the game's own spawner rather than ComboMod: **RightShift+C+L** arms the dev cheats, then
RightShift + **I** (heirlooms), **C** (consumables), **B** (buildings). It is a full picker with a
button per entry, sorted by category and name, and it already knows every valid tag. The API is
`HeirloomsPanel.AddHeirloom(GameTag)` (gems and tomes auto-route to the Jewellery Box and
Bookshelf) and `ConsumablesPanel.AddConsumable(GameTag)`.

### What is editable

The 17 numeric knobs plus Rarity. Collection-valued knobs (MinorCategories, ValidTriggers) and
MajorCategory are API-only — they are not meaningfully editable as text. Vanilla ranges for every
knob are in [docs/modding-surface.html](docs/modding-surface.html); note the editor does not clamp
you to them, because the game does not either.

## Building

Requires the .NET SDK. This machine has it via scoop rather than on PATH:

```bash
~/scoop/apps/dotnet-sdk/current/dotnet.exe build src/ComboMod.SampleTweaks/ComboMod.SampleTweaks.csproj -c Release
```

`NuGet.config` adds the BepInEx feed — `BepInEx.Core` is not published to nuget.org. Game
assemblies are referenced straight out of the Steam install with `Private=false`, so nothing from
the game is ever copied into the output.

Target framework is **netstandard2.1**; the game's assemblies require it and 2.0 fails to link.

## Installing

BepInEx 5.4.23.3 (x64, Unity Mono) is already installed in the game directory. To deploy a build:

```bash
cp src/*/bin/Release/ComboMod.*.dll \
   "/c/Program Files (x86)/Steam/steamapps/common/Combolands/BepInEx/plugins/ComboMod/"
```

Logs land in `<game>/BepInEx/LogOutput.log`. A working load looks like:

```
[Info :ComboMod Core] Game assembly matches the audited build (24930533).
[Info :ComboMod Core]   Bakery.cooldownParam: 5 -> 3
[Info :ComboMod Core]   Blacksmith.rarity: Rare -> Common
[Info :ComboMod Core] Achievement guard engaged; Steam achievements are suppressed for this session.
```

Every tune reports its actual before/after value. A tune that changed nothing logs a warning
instead — without that, a typo'd tag and a working tune look identical.

### Uninstalling

Delete `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and the `BepInEx/` folder from
the game directory. Nothing else in the install is touched, and Steam's file integrity check stays
green.

## Safety features

| Feature | What it does |
|---|---|
| Vanilla-tag guard | `Tune` throws on any tag outside the vanilla `GameTag` enum |
| Version gate | SHA-256 of `Assembly-CSharp.dll` vs the audited build; warns, or refuses if configured |
| Achievement guard | Swaps `AchievementsHandler.achievementPlatform` for a no-op so modded runs cannot unlock Steam achievements |
| Cache invalidation | Calls `BuildingExtensions.ResetCaches()` after applying, so buildings already on the map pick up new stats |
| Change reporting | Logs the real before/after of every field written |

### What the achievement guard does not do

It stops achievements reaching Steam. It does **not** stop the run being recorded in
`Unlocks.save`, which still tracks victories and lifetime counters. Nothing there is corrupting —
a rebalance never introduces an unresolvable tag — but a modded win still counts locally.

## Configuration

`BepInEx/config/dev.combolands.combomod.core.cfg`:

- `RefuseOnVersionMismatch` (default `false`) — refuse to patch when the game assembly hash does
  not match the audited build. Off by default because a mismatch usually still works, and any
  field that genuinely moved is reported by name at tune time.
- `SuppressAchievements` (default `true`).
- `PanelKey` (default `F6`) — opens the panel.
- `AbToggleKey` (default `F7`) — flips every tune on/off for A/B comparison.

Set the BepInEx log level to `Debug` to see per-pass diagnostics (`[apply buildings] tags=3
enabled=3/3`, plus each behaviour instance and whether vanilla was restored first). Useful
because the game re-initialises behaviours from three separate call sites, so apply passes are
more frequent than you would expect.

## Scope

This is the save-safe tier only: **rebalancing existing content**. Adding new buildings, items, or
categories requires minting new `GameTag` values, which is a different and much larger problem —
roughly 13 `Enum.GetValues(typeof(GameTag))` call sites need patching, plus a stable id allocator
and sidecar save manifests. That work is deliberately not started here.
