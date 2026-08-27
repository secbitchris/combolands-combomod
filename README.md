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

**Download `ComboMod-installer.zip`, extract anywhere, double-click `Install ComboMod.bat`.**

That is the whole thing. It finds your Combolands install through Steam — including on a second
drive — downloads BepInEx, verifies it against a known SHA-256 before extracting anything, and
copies the plugins in. Run it again any time; it only replaces what it owns.

`Uninstall ComboMod.bat` reverses it completely. Any balance packs you wrote are copied to a
dated folder in your Documents first, because they live inside `BepInEx\config` and would
otherwise be removed along with it.

Options, if you want them:

```powershell
.\Install-ComboMod.ps1 -CoreOnly                     # packs only, no in-game UI
.\Install-ComboMod.ps1 -SkipCheats                   # framework + editor, no cheat menu
.\Install-ComboMod.ps1 -GamePath "D:\Games\Combolands"
.\Uninstall-ComboMod.ps1 -KeepBepInEx                # other mods still need it
```

### By hand

1. [BepInEx 5.4.23.3 (x64, Unity Mono)](https://github.com/BepInEx/BepInEx/releases) into the
   game folder, next to `Combolands.exe`.
2. Run the game once so BepInEx creates its folders, then quit.
3. Plugin DLLs into `BepInEx/plugins/ComboMod/`.

The step people get wrong is (1) — extracting one folder too deep, which fails silently: the game
launches and nothing happens. That is the failure the installer exists to remove.

Three plugins, installed separately. Each adds to the one before it:

| | | |
|---|---|---|
| **ComboMod** | `ComboMod.Core.dll` | The framework. Loads balance packs, applies tunes, guards achievements. **No UI.** |
| **ComboMod Editor** | `ComboMod.Editor.dll` | The panel (**F6**): browse and tune stats, manage packs. Nothing here can touch a save. |
| **ComboMod Cheats** | `ComboMod.Cheats.dll` | Run editing and giving items. **Writes to your save.** |

The split is the point. Someone who wants to rebalance buildings should not have to install a
money editor to do it, and the boundary is the same one the save format draws: Core and Editor
are Tier 1, Cheats is Tier 2.

Cheats contributes its tabs through `PanelTabs.Register`, so the dependency runs one way — Cheats
knows about Editor, never the reverse.

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

### Global economy

A pack can also reshape the run-wide economy through an `[economy]` section. These are Tier 1
too — draft weights and prices are recomputed from code on every lookup, never serialized.

```ini
[economy]
draft.legendary = 0.08     # vanilla 0.00 - Legendary can never be drafted
draft.rare      = 0.12     # vanilla 0.05
drift.rare      = 0.25     # vanilla 0.15 per city size
shop.blueprint  = 20       # vanilla 4
sellratio       = 0.75     # vanilla 0.5
```

| Key | Vanilla | |
|---|---|---|
| `draft.common` / `.uncommon` / `.rare` / `.masterwork` / `.legendary` | 0.70 / 0.24 / 0.05 / 0.01 / **0.00** | Building draft weight |
| `item.common` / `.uncommon` / `.rare` | 0.60 / 0.30 / 0.10 | Item draft weight |
| `drift.common` / `.uncommon` / `.rare` / `.masterwork` | −0.03 / +0.05 / +0.15 / +0.15 | Change per city size |
| `shop.heirloom` / `.favour` / `.blueprint` / `.supply` / `.spell` | 20 / 6 / 4 / 4 / 2 | Shop card weights (relative) |
| `blueprintprice` | 4 | Flat, ignores building and milestone |
| `sellratio` | 0.5 | Fraction of buy price returned |

**Legendary rolls at 0.00 in vanilla**, in both tables — it falls through to the default case, so
the buildings tagged Legendary can never appear in a draft. One line makes them reachable.

These live in `static` classes with hardcoded switch expressions, so there is no field to reflect
on; they are Harmony prefixes that consult a table and fall through to the original whenever no
override is set. An install with no economy pack behaves byte-identically to vanilla.

### Difficulty curve

`[milestones]` sets what each milestone demands. Also Tier 1 — the thresholds live on a
ScriptableObject that is never written to disk; the save stores only `MilestoneIndex`.

```ini
[milestones]
scale        = 0.8      # multiply every milestone
Hamlet       = 250      # or set one outright
Village.rankA = 1500    # or just one of its three thresholds
```

City sizes are `Start`, `Dwelling`, `Hamlet`, `Village`, `SmallTown`, `LargeTown`, `SmallCity`,
`BigCity`, `CapitalCity`, `Metropolis`.

Each milestone carries **three** thresholds — a base one, and higher ones used at Yeoman and
Governor rank. A bare value sets all three, because setting only the base leaves a ranked player
on untouched numbers, looking at a mod that appears to do nothing. Add `.base`, `.rankA` or
`.rankB` to target one. `scale` applies after any explicit value, and floors at 1 since a
milestone of 0 would complete instantly.

One caveat: the ScriptableObject is shared for the session, so a change applies to later runs too
until the game restarts.

### Placement rules

`PlaceOn` inside a building's section sets which tile types it can be built on:

```ini
[building.Bakery]
PlaceOn = Grass, Sand, Shore
```

Tile types are `Grass`, `Sand`, `Shore`, `Ocean`. Buildings only — items have no such field.

**Widening the set permits a placement, it does not force one.**
`GetTileTypesCanBePlacedOn` is virtual and some behaviours override it outright (Enclave does),
and `CanBeBuiltOn` can still veto for its own reasons.

### What a piece targets

The deepest lever here: not how hard a building hits, but what it aims at.

```ini
[building.Bakery]
TargetCategories = Farm:3, Nature:1
TargetTags       = Windmill:5
TargetTileTypes  = Grass:2, Sand
TargetRarities   = Rare:4
```

Each entry is `name:score`; the score is optional and defaults to 0, matching the game's own
no-score overload. Order matters — it becomes the `TargetNumber` the game assigns.

Names are validated per kind, so a mistake says which kind it failed as:

```
'Farm' is not a rarity; that target line was skipped.
```

### Your edits are kept

Anything you change in the panel is written to `_live-edits.pack` a couple of seconds after you
stop typing, and on quit. It is restored on the next launch, layered on top of packs exactly as
it was when you made it.

Tuning is real work and the Save-as-pack button is easy to miss. Losing an hour of it to a habit
you had not learned yet is how people stop using a tool.

The file is a normal pack — rename it and it becomes a shareable one.

### When two packs disagree

Later wins, which is usually right and always silent. So a pack that overwrites another pack's
global setting says so:

```
'Quick Run' overrides 'Gentler Curve' for the milestone scale (0.7 -> 0.6).
Later packs win; disable one in the Packs tab.
```

Per-building stats layer the same way, and the Registered tunes tab shows every source, so you
can see who won without reading a log.

### Packs that ship with it

Three, in `packs/` in the repo. Copy the ones you want into your packs folder.

| | |
|---|---|
| **Gentler Curve** | Every milestone asks 30% less. One line, one obvious effect. |
| **Legendary Unlocked** | Legendary buildings become draftable — they roll at 0.00 in vanilla, so they can never appear at all. |
| **Quick Run** | Shorter milestones, cheaper blueprints, a shop that favours them. Opinionated: it changes pace, not just numbers. |

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

### Packs

Loaded packs with per-pack switches, author, version, change count, and any parse warnings.
Reload, and open the packs folder.

### Run and Give (ComboMod Cheats only)

**Run** — money, weeks remaining, score, milestone target, rerolls/removes/dismisses/rewinds, and
inventory slot counts. Slots are **add-only**: the game has no `RemoveSlot`, so a count rises and
never falls.

**Give** — search anything givable and hand it over:

| Kind | Becomes |
|---|---|
| Item / heirloom | an heirloom (gems auto-route to the Jewellery Box, tomes to the Bookshelf) |
| Consumable / spell / favour | a consumable |
| Building | a **blueprint card**, stacking onto an existing one |

Each row disables itself with a reason when it cannot work.

**Manage** — what you are actually holding, with **Sell** and **Remove** per entry, slot counts
against vanilla, and **Trim to vanilla**.

This exists because ComboMod caused the problem it solves. The game has **no `RemoveSlot` at
all**, and its inventory panels lay out for a handful of slots, so adding a dozen makes the
normal UI unusable. Both `Slots` properties return the live backing list, so a slot can be
removed by taking it out and destroying its GameObject.

Only **empty** slots are removed, last first. Emptying a slot destroys an item, and that should
be a deliberate choice rather than a side effect of trimming — so `Trim to vanilla` reports how
many it actually managed.

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

`MinorCategories` and `ValidTriggers` are editable in the panel — expand **Sets** under a
piece's stats for a multi-select. `MajorCategory` remains API-only.

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
dotnet build src/ComboMod.Cheats/ComboMod.Cheats.csproj -c Release   # builds all three
bash packaging/build-packages.sh                                     # Thunderstore zips
```

`NuGet.config` adds the BepInEx feed (`BepInEx.Core` is not on nuget.org). Game assemblies are
referenced straight out of the Steam install with `Private=false`, so **nothing from the game is
ever redistributed**. Set `GameManaged` in the csproj if your install is elsewhere.

Target framework is **netstandard2.1** — the game's assemblies require it and 2.0 fails to link.

---

## Version gate

ComboMod reflects on private field names, which are not API and can change in any patch.
`SafetyGate` hashes `Assembly-CSharp.dll` against a list of known-good builds.

An unrecognised hash is only the first question. The second — and the one that decides whether
the mod actually works — is whether every member ComboMod reflects on is still present, so on an
unknown build it runs an **integrity check** and reports either *all N members present, should
work normally* or exactly which ones are missing. "The build changed" and "the mod is broken" are
very different statements, and a user deserves to be told which one they have.

Set `RefuseOnVersionMismatch` to refuse loading instead of warning.

A mismatch is usually harmless: when Combolands patched from 24930533 to 24951781 during
development, re-auditing found every reflected field, both patched methods, and all balance data
unchanged.

Packs and live edits degrade **per field** — a renamed stat costs that one stat and is reported by
name. Code tunes cannot do this, because a lambda that throws mid-way cannot resume.

Set the BepInEx log level to `Debug` for per-pass diagnostics — which behaviour instance was
touched and whether vanilla was restored first. Useful because the game re-initialises behaviours
from three separate call sites (`BehavioursController`, `ItemPool`, `BuildingCategoryVisualization`),
so apply passes are more frequent than you would expect.

---

## Performance

Two optimisations, both off-by-config, both held to a stricter bar than a rebalance: a wrong tune
is visible, a wrong "optimisation" corrupts scoring quietly.

**`Performance.FastMapLoad`** — `BuildBuildingAt` ends by rebuilding the entire spatial index,
walking every placed building and every tile in its range. During a load that runs once per
building: 641 full rebuilds, ~33 seconds on a 1,188-building board. It is safe to collapse them
because `InitializeGridFromSave` already ends with its own rebuild that supersedes every
intermediate one, no triggers run during the load, and buildings are placed with
`triggerOnBuild: false`. Load time went from 33 seconds to instant.

**`Performance.OptimiseScoring`** — `UpdateSumOnScreen` sums every live scorer into a private
field with no readers anywhere in the assembly, once per scorer tick, which is quadratic during a
cascade. And `ProcessTrigger` walks its behaviour dictionary comparing keys rather than looking
one up, which the game already does correctly in `GetBehaviourFor`.

**`Performance.Profile`** — call counts, totals, worst-case per call, frame statistics and GC
correlation, logged every 5 seconds. A diagnostic; it adds a timestamp pair per call on hot paths,
so leave it off.

### What is not fixable from a mod

Frame hitches of ~175 ms when a building spawns mid-cascade are a genuine index rebuild caused by
a genuine board change. Making it incremental would mean assuming a new building cannot alter
other buildings' ranges — range-modifier buildings do exactly that, which is presumably why the
game rebuilds wholesale. Triggers read the index during a cascade, so deferring it would feed
stale range data into scoring. The only lever there is board size, and the cost is superlinear in
it.

## Configuration

One config file per plugin, under `BepInEx/config/`, written on first run.

| Key | Plugin | Default | |
|---|---|---|---|
| `RefuseOnVersionMismatch` | Core | `false` | Refuse to load on an unrecognised game build rather than warning |
| `SuppressAchievements` | Core | `true` | Stop modded runs unlocking Steam achievements |
| `OptimiseScoring` | Core | `true` | The two provably-equivalent hot-path replacements |
| `FastMapLoad` | Core | `true` | Collapse redundant index rebuilds during a map load |
| `Profile` | Core | `false` | Frame and method timings logged every 5s. Diagnostic; leave off |
| `PanelKey` | Editor | `F6` | Opens the panel |
| `AbToggleKey` | Editor | `F7` | Flips every tune on and off, for A/B against vanilla |
| `Scale` | Editor | `0` | Panel size. `0` derives it from screen height on first paint |

## What this has actually been tested on

One person, one save, one machine. Verified against Combolands builds **24930533** and
**24951781** on Windows 11.

What is genuinely exercised: pack loading and per-line error recovery, precedence across all
three layers, live editing applying to already-placed buildings, the integrity check on an
unrecognised build, giving items, slot add and removal, and the map-load optimisation. What is
not: any other machine, any other save shape, multiple packs from different authors fighting over
the same building, or a board that is not a full 44x27.

`src/ComboMod.SampleTweaks` is a **format demo, not balance advice** — three values chosen to
exercise every kind of knob. It is deliberately excluded from the published packages.

## Scope

Rebalancing existing content, editing run state, and giving existing items. **Adding new
buildings or items is out of scope** — that needs new `GameTag` values, which means a stable id
allocator, postfixes on ~13 `Enum.GetValues(typeof(GameTag))` call sites, sidecar save manifests,
and a load-failure guard around `IOManager.LoadFile` (which currently has no try/catch at all).

The game ships its own dev cheat menu, unrelated to this mod: **RightShift+C+L** arms it, then
RightShift + **G** (money), **W** (weeks), **E** (end milestone), **B**/**I**/**C** (spawn
pickers), **PageUp** (fast scoring). The spawner is better than anything ComboMod would rebuild.
