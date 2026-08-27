# Handoff

Everything a new session — human or otherwise — needs to pick this up cold. Written 2026-08-27.

`README.md` explains what the mod does for a user. This is the other half: why it is built the
way it is, what has been verified, and what will bite you.

---

## Where things are

| | |
|---|---|
| Repo | `this checkout` → `github.com/secbitchris/combolands-combomod` (**public**) |
| Game | `C:\Program Files (x86)\Steam\steamapps\common\Combolands` (Steam appid 4075620) |
| Decompiled game source | scratchpad `combolands-src-new/` — regenerate with `ilspycmd` if gone |
| Audit document | `docs/modding-surface.html`, also published as an Artifact |
| Backup of pre-rewrite git history | `Documents/combolands-combomod-backup-20260827-0823.bundle` |

**Toolchain quirks on this machine.** The .NET SDK is not on `PATH` — it is at
`~/scoop/apps/dotnet-sdk/current/dotnet.exe`. `ilspycmd` is at `~/.dotnet/tools/ilspycmd.exe`.
`zip` is not installed, which is why the packaging script falls back to Python.

```bash
D=~/scoop/apps/dotnet-sdk/current/dotnet.exe
"$D" build src/ComboMod.Cheats/ComboMod.Cheats.csproj -c Release   # builds all three
bash packaging/build-packages.sh                                   # four release zips
```

Deploy = copy the three DLLs into `<game>/BepInEx/plugins/ComboMod/`. **The game must be closed**
or the files are locked. Logs land in `<game>/BepInEx/LogOutput.log`.

---

## The one fact everything rests on

The run save stores per-piece **deltas only** — `LocalRangeChanges`, `LocalMultChanges`, `Count`,
`StoredValue`. **No base stat is serialized anywhere.** Base values are re-read from code on every
load.

That is why rebalancing cannot corrupt a save and is fully reversible, and it is the reason this
project is shaped the way it is. If that ever stops being true, the safety claim in the README is
void and the whole design needs revisiting.

The thing that would break it is a new `GameTag`: `JsonUtility` writes enums as bare integers an
unmodded client cannot resolve. Both entry points — `ComboModApi.Tune` and the pack parser —
reject non-vanilla tags, so the guarantee is mechanical rather than a matter of discipline. **Do
not weaken those checks.**

---

## Architecture

Three plugins, dependencies running one way only.

```
ComboMod.Core      framework, headless, no UI references at all
  └ ComboMod.Editor    the F6 panel (Tier 1 only: cannot touch a save)
      └ ComboMod.Cheats  run editing, giving items (Tier 2: writes to the save)
```

The split follows the boundary the save format already draws. Cheats contributes its tabs through
`PanelTabs.Register`, so Editor never references Cheats and installs standalone.

| File | Lines | Role |
|---|---|---|
| `Core/ComboModApi.cs` | 686 | registry, layered apply, restore snapshots |
| `Core/BalancePack.cs` | 504 | pack format and parser |
| `Core/PackLoader.cs` | 416 | discovery, registration, hot reload |
| `Core/Tuner.cs` | 341 | reflection over base stats |
| `Core/MilestoneTuning.cs` | 296 | difficulty curve |
| `Core/SafetyGate.cs` | 253 | version gate + integrity check |
| `Core/Economy.cs` + `EconomyPatches.cs` | 347 | global economy |
| `Core/Profiler.cs` + `ProfilerPatches.cs` | 328 | diagnostics, off by default |
| `Core/Plugin.cs` | 199 | entry point, config, Harmony |
| `Core/LiveEditStore.cs` | 147 | live edits across restarts |
| `Core/PerformancePatches.cs` | 125 | two hot-path replacements |
| `Core/LoadPatches.cs` | 108 | the 33s→instant load fix |
| `Editor/ModPanel.cs` | 810 | the panel; largest file, five tabs |
| `Cheats/*` | ~1200 | run state, inventory, give |

**Precedence is explicit**: `code → pack → live edit`, later wins, keyed on `TuneSourceKind` — not
on plugin load order. It used to fall out of load order, which let a shipped DLL silently override
a user's own pack.

---

## Traps, learned the hard way

Each of these cost real time. They are commented at the point of danger in the code, but they are
worth knowing before you touch anything.

**The game rebuilds behaviour dictionaries on every scene load**, from *three* call sites
(`BehavioursController`, `ItemPool`, `BuildingCategoryVisualization`). A one-shot registration at
startup appears to work and then silently stops. Everything re-applies via Harmony postfix.

**Stats are cached per placed building** behind sentinel values. Change a base stat and buildings
already on the map keep serving stale numbers until `BuildingExtensions.ResetCaches()`. Invalidation
is coalesced to once per frame — it walks every building, and it was running twice per keystroke.

**`ChooseBag.RemoveElement` only removes entries whose weight is `> 0`.** Set a draft weight to
exactly 0 and the entry becomes unremovable, silently shrinking the number of draft choices offered.
Use `ComboModApi.SuppressionWeight`.

**The milestone target is two numbers.** `GameController.ScoreRequired` is the win check;
`MilestoneManager.CurrentRequiredScore` is what the HUD prints. Setting only the first gives a real
target the UI never shows.

**The HUD caches.** `UpdateGoalText` runs only at milestone start and week end, so a mid-week edit
is correct in memory and invisible on screen. Every setter must refresh what it owns.

**A Harmony postfix does not run when the original throws.** Use `[HarmonyFinalizer]` for anything
that must clean up — a latched suppression flag disabled index rebuilds for a whole session.

**Patch each group in a guard.** An ambiguous overload
(`InstantiateAndBuildBuildingAt` has two) threw out of `Awake` and silently disabled every feature
after it, with nothing in the log.

**`AchievementsHandler` is a scene singleton.** Leaving a menu destroys the instance you neutered
and builds a fresh one with the real Steam platform. The guard tracks the instance and re-arms.

---

## What is verified, and what is not

**Verified in the running game**: pack loading and per-line error recovery; precedence across all
three layers; live editing reaching already-placed buildings; the integrity check on an
unrecognised build (forced by blanking the hash); giving items; slot add and removal; the
map-load fix; install and uninstall round trip against a byte-identical vanilla folder.

**Verified against the save data, not yet in the running game**: the 0.2.1 plateau/canal fix
(`MapFixPatches.cs`). The vanilla bug and its signature were confirmed by decompiling the load
path and diffing a real save (106 plateau tiles in tile data, 2 in `PlateauCoords`); the patch
builds and is deployed, but no session has loaded a save through it yet. First launch should log
`Rebuilt N plateau ... sprite(s)` once, then never again.

**Not verified**: any other machine, any other save shape, competing packs from different authors,
boards smaller than the 44×27 tested on. Panel *buttons* are largely untested — synthetic clicks
do not reach Unity, so interactive paths were exercised through hotkeys or by planting files.

Tested against builds **24930533** and **24951781** only.

---

## Performance: what was actually true

Three explanations were offered before one survived contact with data. Worth reading as a caution.

Wrong: "trigger dispatch arithmetic" — `ProcessTrigger` costs **0.000 ms/call**.
Wrong: "garbage collection" — frame spikes occur on frames with **no collection**.
Right: `BuildingController.RefreshBuildings` rebuilds the entire spatial index on **every**
building placement. During a load that ran 641 times for **33 seconds**; collapsing it made loading
instant.

Average framerate during scoring is **170–230 fps**. The remaining ~175 ms hitch per building
spawn mid-cascade is a genuine rebuild caused by a genuine board change and is **not safely
fixable from a mod** — range-modifier buildings mean a new building can alter other buildings'
ranges, and triggers read the index mid-cascade.

`Performance.Profile` turns the profiler on. It reports call counts, totals, **worst-case per
call** (which is what found this — a 175 ms outlier is invisible in an average), frame stats and
GC correlation.

---

## Open work

**Out of scope by decision.** Adding new buildings or items needs new `GameTag` values: a stable id
allocator, postfixes on ~13 `Enum.GetValues(typeof(GameTag))` call sites, sidecar save manifests,
and a load-failure guard around `IOManager.LoadFile` (which has no try/catch at all). Starting this
means owning the Tier 3 problem properly.

**Worth doing.**

- **Thunderstore community request for Combolands.** There is none, which is why installation is a
  script rather than one click through r2modman. The manifests and package layout are already
  correct and waiting.
- **Real icons.** `tools/make-icons.py` generates deliberate placeholders.
- Splitting `ModPanel.cs` (810 lines, five tabs). Cosmetic.
- `MajorCategory` is still API-only.

**Reclassified, do not re-pitch as free content.** `GamePieceCategory.Masonry`,
`TriggerType.BuildingUpgradeUsed` and `TriggerType.RemoveGained` are declared but referenced
**nowhere** in the game. Nothing raises those triggers and nothing reads that category, so they are
a free *namespace* for mods to use between themselves, not usable content.

---

## Housekeeping

Git history was rewritten on 2026-08-27 to replace a personal email with
`6148221+secbitchris@users.noreply.github.com` and strip session URLs from commit messages. Tree
hashes are identical before and after — metadata only. `git config user.email` is set locally to
the noreply address.

Still worth enabling account-wide: GitHub → Settings → Emails → *Block command line pushes that
expose my email*.
