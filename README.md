# ZTX Ramming Damage

Adds ship-vs-ship collision damage to Cosmoteer. Ships colliding above a configurable speed damage each other based on each part's penetration resistance, and every destroyed part converts into a contact-point impulse that pushes and spins the bodies apart.

## What it does

- **Damage by pen-ratio.** Each side takes `MaxHealth × (other-side pen / max pen)`. Pen-7 armour against a pen-1 corridor kills the corridor in one hit and costs the armour ~14% of its health. Equal pens destroy each other.
- **Directional pushback.** Destroyed HP becomes a contact-point impulse, so heavier ships slow less and glancing hits spin rather than stop.
- **Head-on bonus.** Committed head-on collisions deal up to 4× the damage of a one-sided ram.
- **Armour shadowing.** Armour protects lower-pen parts behind it on the same ship, so internals don't die alongside the hull when geometry clips through.
- **Per-category ramming rules.** Separate switches for asteroids, wreckage, neutrals, allies, your own ships, and enemies.
- **Career hostility.** Ramming a faction attributes the damage to you, so war triggers and reputation react as they would to weapons fire.
- **Mass tuning.** Optional per-mass speed gates and impulse damping so fighters and debris don't get flung at absurd speeds by capital ships.

## Requirements

- **Cosmoteer 0.30.4** or compatible
- **YAML (Yet Another Mod Loader)** — [Workshop 3577650065](https://steamcommunity.com/sharedfiles/filedetails/?id=3577650065), required to load the DLL

## Installation

1. Subscribe to YAML, then to this mod.
2. Launch Cosmoteer, open **Mods**, enable **Ramming Damage**.
3. When YAML asks, choose **Trust** for the DLL.
4. Restart the game.

**Multiplayer:** every player needs the mod enabled with matching `config.rules` values. Cosmoteer uses deterministic lockstep, so mismatched tunables desync on the first ram.

## Tuning

Everything lives in `config.rules` next to the DLL. Edit, restart Cosmoteer — no rebuild needed. Every knob is commented in that file; this is the short version.

### Core

| Knob | Default | Effect |
|---|---|---|
| `MinClosingSpeed` | 50 | Relative speed needed to start ramming |
| `MinSustainSpeed` | 5 | Relative speed needed to keep ramming once started |
| `HpToEnergy` | 0.5 | Master energy knob — impulse budget per HP destroyed |
| `PushbackFormula` | 1 | 0 = Asymmetric, 1 = NewtonSum |
| `LinearMultiplier` | 1.0 | Linear-push share of each impulse |
| `RotationMultiplier` | 1.0 | Rotational share. Try 3-10 for visibly spinny glancing hits. |
| `SlowdownHeadOnFactor` | 0.71 | Head-on amplification. 0.71 gives roughly 4×; lower amplifies more; 1.0 disables it. |
| `SustainGraceTicks` | 30 | Ticks the sustain threshold persists after contact is lost (30 ticks = 1 second) |

### What can be rammed

`UseRamDamageGates = true` enables the per-category switches below. Set it `false` to make everything rammable and ignore them entirely.

| Knob | Default | Covers |
|---|---|---|
| `RamDamageEnemies` | true | Anyone hostile to you, including barbarians |
| `RamDamageJunk` | true | Wreckage, derelicts, abandoned ships |
| `RamDamageAsteroids` | true | Asteroids and megaroids |
| `RamDamageNeutral` | true | FTL gates and faction beacons |
| `RamDamageAllies` | false | Allies, truces, protection agreements |
| `RamDamageSelf` | false | Your own other ships |

Each gate applies to the whole collision: if you can ram an asteroid, the asteroid can equally damage you. These are independent of the game's Friendly Fire option, which governs weapons rather than ramming.

Invulnerable ships take no damage but can still ram-damage others, matching how vanilla weapons behave.

### Stall mechanic (off by default)

`StallEnabled = false`. It briefly freezes a ramming ship to stop fast ships tunnelling through hulls. It's no longer needed — damage now keeps pace with movement on its own — and it costs a lot in feel. If very fast ships do tunnel, lower `MaxPhaseLayers` before turning the stall back on — it caps phase-through to roughly that many tiles.

### Mass tables

Both are optional and flip on and off without erasing tuned values.

```
ImpulseReductionTiers = 5:0.05, 500:1.0
MassClosingSpeedTiers = 50:5, 100:20, 500:35, 2000:50
```

`MassCap:Value` pairs, ascending, linearly interpolated. The impulse example means a 5t fragment takes 5% of the calculated impulse and a 500t fighter takes all of it. The speed example lets light ships ram at low speed while heavy ones need real velocity.

## Reporting issues

The mod logs init lines and a per-session summary by default. Every line is prefixed `[ZTX.Ramming v<version>]`, so reports carry the build automatically.

Logs live in `<Saved Games>\Cosmoteer\<your-id>\Logs\`, and the mod writes to **both** files there:

- `log<timestamp>_modloader.txt` — startup: the full config dump, self-test result, and version banner. Config loading happens before the game's own logger exists, so it lands here.
- `log <timestamp>.txt` — everything after that: gameplay, errors, and the `Sim totals` summary written when you leave a game.

For per-hit detail, set `DebugLog = true` and restart. When reporting a problem, please include **both** files — the config dump in the modloader log is what tells us which settings produced the behaviour.

## Source

Full C# source: https://github.com/ZTXDragon/Ramming-damage

Builds with .NET SDK 10 and HarmonyLib. Run `build_and_install.ps1` with Cosmoteer closed to compile and install in one step. MIT licensed.

## Compatibility

Tested in Creative and Career. No vanilla files are modified and no base-game behaviour is overridden — every hook the mod installs is observe-only. Part rules, ship designs, and save format are untouched.

## Notes

- AI was used during code generation of this mod.

## License

MIT — see `LICENSE`.
