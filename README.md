# Follower Alive Status

A TurboHUD plugin for Diablo III. A skull on your HUD turns red the moment your follower goes
down, green when they get back up, and stays grey when you have no follower hired — with a
counter that tallies **their** deaths and never yours.

![Follower Alive Status — the three icon states](docs/follower-status-icons.png)

Solo play only: followers cannot be brought into multiplayer games, so the icon stays grey there.

---

## What it does

- A skull icon on your HUD whose colour reflects the follower's state.
- A counter beside it, stepped up **only** on a confirmed *alive → dead* transition.
- Your own deaths, loading screens, zone changes and teleport chains never touch the counter.
- The town follower NPCs are ignored — only a genuinely hired hireling is tracked.
- Three icon styles: skull, plain dot, or the follower's portrait.
- Seven placements, including one docked under the RBH session panel.

## Where it sits

By default the icon docks under the RBH session tracker. RBH draws that panel from the top-left
corner of the minimap element downwards, so this plugin anchors to the same element and drops by a
ratio you can tune. **No RBH file is read or modified for this.**

```
┌───────────────────────┐
│ Status:   Wait Botting│
│ Duration: 00:17:22    │
│ Rifts:    0R 0,00R/H  │  RBH session panel
│ Greater:  0R 0,00R/H  │
│ Nephalem: 0R 0,00R/H  │
│                       │
│ [O] x2                │  <- this plugin
└───────────────────────┘
   minimap area
```

The drop is expressed as a number of RBH tracker lines, and the height of a line is measured live
rather than assumed, so **the icon keeps its place when you resize the game window**. That detail
matters: RBH sizes its font to fit the minimap width but stops shrinking at 13.5 pixels, an
absolute floor, so its panel takes a larger share of the minimap on a small window than on a large
one. A fixed fraction of the minimap drifts into the panel as soon as the window changes size.

Other anchors: `UnderPortrait`, `LeftOfHealthGlobe`, `RightOfResourceGlobe`, `AboveSkillBar`,
`UnderMinimapClock`, and `Custom` for free placement by screen ratio.

## Install

1. Copy the `GuiSquare` folder into your TurboHUD `plugins` directory. The plugin is
   self-contained — no other files, no textures.

2. **If you run RBH**, its plugin manager disables everything it does not know about. Add one line
   to the `Enable_Plugins` list in `plugins/RosbotHelper/Config/Manager_Config.cs`:

   ```csharp
   "Turbo.Plugins.GuiSquare.FollowerAliveStatusPlugin"
   ```

3. Restart TurboHUD. If the icon overlaps the session panel or floats too low, set
   `RbhPanelLineCount` to the number of lines in your own session panel, plus one.

## Configuration

Rename `GuiSquare/FollowerAliveStatusCustomizer.txt` to `.cs` to change any setting without editing
the plugin itself. RBH users must whitelist the customizer too:
`"Turbo.Plugins.GuiSquare.FollowerAliveStatusCustomizer"`.

| Option | Default | What it controls |
| --- | --- | --- |
| `Position` | `BelowRbhSessionPanel` | Where the icon docks |
| `RbhPanelLineCount` | `8` | Drop below the minimap top edge, in RBH tracker lines. Resize-proof |
| `RbhPanelHeightRatio` | `0.46` | Legacy drop as a fraction of minimap height, used only when `RbhPanelLineCount` is `0` |
| `IconStyle` | `Skull` | `Skull`, `Dot` or `Portrait` |
| `IconSizeRatio` | `0.024` | Icon size, as a fraction of screen height |
| `ShowCounter` / `CounterOnRight` | `true` / `true` | Death counter, beside or inside the icon |
| `CounterPrefix` | `×` | Drawn in front of the number, so `×2` rather than a lone `2` |
| `BlinkWhenDead` | `true` | Pulse the icon while the follower is down |
| `HideWhenNoFollower` | `false` | Hide entirely instead of showing grey |
| `ResetCounterOnNewGame` | `false` | Session total, or per game |
| `ResetCounterOnClick` | `true` | Allow resetting the counter from the icon |
| `ResetClickRequiresCtrl` | `true` | Whether the reset click needs Ctrl held |
| `ResetClickButton` | `Left` | Mouse button used for the reset |
| `SpeakOnDeath` | `false` | Spoken alert when the follower dies |
| `DebugEnabled` | `false` | On-screen diagnostic panel (see below) |

Colours are plain TurboHUD brushes and can be replaced: `AliveBrush`, `DeadBrush`,
`NoFollowerBrush`, `IconDetailBrush`.

### Resetting the counter

The counter is a running session total: it survives quitting to the menu and starting another
game, and only goes back to zero when TurboHUD restarts — or when you reset it yourself.

**Ctrl + click the icon** to reset it. Ctrl is required by default because the icon sits in the
top-left area where you click to move, and an accidental reset would be unrecoverable. The hover
tooltip always states the gesture currently in effect, and the click is swallowed either way so
your character never walks under the icon.

For a plain click with no modifier, either drop the requirement or move the reset to a button that
cannot be misclicked:

```csharp
plugin.ResetClickRequiresCtrl = false;
// optionally, a button not bound to movement:
plugin.ResetClickButton = System.Windows.Forms.MouseButtons.Middle;
```

## How the detection works

The interesting part of this plugin is not drawing a skull, it is refusing to count the wrong
thing. Two independent signals are read, with a deliberately asymmetric rule between them: the
follower counts as **alive if either source says alive**, and dead only when both agree. A failed
read can therefore never manufacture a death.

| Signal | Role |
| --- | --- |
| `IActor.Hitpoints` | Primary. Verified in game as current health: it drops to `0` on death while the actor stays present, then climbs back on revival. |
| `Hitpoints_Cur` | Secondary. This attribute is **not** exposed on follower actors — it returns `-1` — so it can only ever confirm life, never death. |
| Actor SNO | Strict match on the three hired hirelings: `_hireling_templar` (52693), `_hireling_scoundrel` (52694), `_hireling_enchantress` (4482). The town NPC versions carry different SNOs. |
| Pet item slots | Follower gear in `PetRightHand`…`PetBracers` proves a follower is hired even at a moment the actor is not loaded. |

On top of that, a death is only ever recorded after the plugin has **confirmed the follower alive
first**. An ambiguous moment therefore costs a missed count at worst, never a phantom one.

| Situation | Behaviour |
| --- | --- |
| Your hero dies | State frozen, counting suspended, plus a 1 s blind window after you resurrect |
| Loading screen, menu, paused | Grey, detection disarmed |
| Zone change, rift entry, town portal | The follower must be seen alive again before a death can register, plus a 1.5 s blind window |
| Fast teleport chains | Nothing. Death rests on hitpoints alone — a briefly uncollected actor is a gap, not a corpse |
| Multiplayer game | Grey. Followers cannot be brought along |
| Standing beside the town follower NPCs | Grey. Their SNOs do not match a hired hireling |

### Diagnostic panel

Setting `DebugEnabled = true` prints the raw signals on screen — state, armed flag, hitpoints from
both sources with the min/max observed, the follower actor's SNO and world, equipped follower
items, and a timestamped log of the last five deaths.

## Requirements

TurboHUD with the `Turbo.Plugins` API used here (`IInGameTopPainter`, `IAfterCollectHandler`,
`INewAreaHandler`). No external dependencies.

## License

[MIT](LICENSE) — use it, change it, ship it in your own pack, just keep the notice.
