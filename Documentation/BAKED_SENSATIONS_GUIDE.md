# Authoring and loading OWO baked sensations (.owoauth)

How to create new registered ("baked") OWO sensations and load them with the SDK. A
baked sensation is a named, iconed effect you register with your OWO game id. Once
registered, the OWO app resolves it by id and shows it in its Baked Sensations list
(name, icon, family), and you can trigger it by id.

You do not need baked sensations to fire haptics: a normal `sensation(...)` or a
`.owo` file plays fine as a live sensation. Bake one when you want it to appear named
in the app or to send it compactly by id.

## The `.owoauth` file format

A `.owoauth` file is one or more registration strings joined by `#`. Each string has
five `~`-separated fields:

```
id ~ name ~ reference ~ icon ~ family
```

| Field | What it is | Example |
|---|---|---|
| `id` | integer, unique within your game | `1` |
| `name` | display name shown in the app | `Heavy Impact` |
| `reference` | the actual sensation, in normal wire form | `100,2,100,0,0,0,Impact|0%100,1%100` |
| `icon` | an icon id, or `0` for none | `Impact-2` |
| `family` | grouping label in the app (may be empty) | `Impacts` |

The bundled example, `samples/example.owoauth`:

```
0~Example Impact~100,2,90,0,0,0,Impact|0%100,1%100~Impact-2~Examples
```

That is id `0`, name `Example Impact`, a 100 Hz / 0.2 s / 90% pulse named `Impact`
on both pectorals at 100%, icon `Impact-2` (punch), family `Examples`.

Do not use the structural characters `~ # | & , %` inside a name; they separate
fields.

## The reference sensation

The `reference` is a normal sensation wire string, the same format the SDK and `.owo`
files use:

- Micro-sensation: `frequency,duration_ds,intensity,rampUp_ms,rampDown_ms,exitDelay_ds,name`
  (duration and exit-delay in deciseconds x10; ramps in milliseconds x1000).
- Add muscles: `<micro>|id%intensity,id%intensity,...`
- Sequence of steps: `<sensation>&<sensation>&...`

Muscle ids:

| id | muscle | id | muscle |
|---|---|---|---|
| 0 | Pectoral_R | 1 | Pectoral_L |
| 2 | Abdominal_R | 3 | Abdominal_L |
| 4 | Arm_R | 5 | Arm_L |
| 6 | Dorsal_R | 7 | Dorsal_L |
| 8 | Lumbar_R | 9 | Lumbar_L |

## Template

Copy one line per sensation. Join multiple lines with `#`.

```
<id>~<name>~<freq>,<dur_ds>,<intensity>,<rampUp_ms>,<rampDown_ms>,<exitDelay_ds>,<name>|<muscleId>%<intensity>,...~<icon>~<family>
```

Filled in:

```
1~Heavy Impact~100,2,100,0,0,0,Impact|0%100,1%100~Impact-2~Impacts
2~Soft Pulse~40,3,60,100,100,0,|2%80,3%80~0~Ambient
```

## Authoring, method A: the OWO Sensations Creator (visual)

1. Design the sensation (frequency, intensity, duration, muscles) in the Creator.
2. Turn on the **Bake** toggle. Turn on **Export with muscles** if you targeted
   specific muscles.
3. **Export sensation** and copy the exported registration string.
4. Paste it into your `.owoauth` file. Add more, separated by `#`.

## Authoring, method B: the SDK (programmatic)

Build a `BakedSensation` and write it out. This round-trips with `load_owoauth`.

```python
import owo
from owo import BakedSensation, sensation, Muscle, Icon, save_owoauth

impact = BakedSensation(
    1, "Heavy Impact",
    sensation(100, 0.2, 100, name="Impact").with_muscles(Muscle.PECTORAL_R, Muscle.PECTORAL_L),
    icon=Icon.IMPACT_PUNCH, family="Impacts")

soft = BakedSensation(
    2, "Soft Pulse",
    sensation(40, 0.3, 60, ramp_up_s=0.1, ramp_down_s=0.1).with_muscles(Muscle.ABDOMINAL_R, Muscle.ABDOMINAL_L),
    icon=Icon.EMPTY, family="Ambient")

save_owoauth("my_game.owoauth", [impact, soft])
```

`BakedSensation(id, name, reference, icon=Icon.EMPTY, family="")`. The `reference` is
any sensation you can build; test it live first (send it un-baked) to feel it, then
bake it.

## Icons

Use a standard icon constant, a custom icon id registered with your game, or none.

| Constant | id | Constant | id |
|---|---|---|---|
| `Icon.EMPTY` | `0` | `Icon.IMPACT_BALL` | `Impact-0` |
| `Icon.DEATH` | `Death-0` | `Icon.IMPACT_DART` | `Impact-1` |
| `Icon.SPIDERS` | `Spider-0` | `Icon.IMPACT_PUNCH` | `Impact-2` |
| `Icon.WEIGHT` | `Weight-0` | `Icon.IMPACT_BULLET` | `Impact-3` |
| `Icon.ENVIRONMENT` | `Environment-0` | `Icon.WEAPON_AXE` | `Weapon-0` |
| `Icon.ALERT` | `Alert-0` | `Icon.WEAPON_DAGGER` | `Weapon-1` |
| `Icon.VICTORY` | `Victory-0` | `Icon.WEAPON_GUN` | `Weapon-2` |
| | | `Icon.WEAPON_SUBMACHINEGUN` | `Weapon-3` |

Custom icons registered with your own OWO game (for example a `mygame-0` id that
your game defines) are used by their exact id string. `0` or `Icon.EMPTY` means no
icon.

## Families

A family is a free-form label the app groups sensations under (for example
`Default`, `Impacts`, `Ambient`). Leave it empty for no grouping.

## Loading them

### With the SDK / in your app

```python
import owo

regs = owo.load_owoauth("my_game.owoauth")
client = owo.OWOClient(game_id="<your-game-id>", registered_sensations=regs)
client.open()
client.auto_connect(timeout=15)
```

On connect, the SDK sends these registrations in the AUTH handshake, so the app lists
them under their names and icons. Trigger a registered one by passing it to `send`
(it transmits as just its id):

```python
client.send(regs[0])                 # play "Heavy Impact" by id
client.send(regs[0], Muscle.ARM_R)   # or re-target its muscles
```

Use your real game id so names and icons resolve. Anonymous id `"0"` still plays live
sensations but will not resolve registered names or icons.

### With the test rig

`test_rig.py` loads `samples/example.owoauth` by default. Point it at another file
with `--owoauth`:

```
py -3.11 test_rig.py --visualizer --owoauth my_game.owoauth
```

## Gotchas

- **Unique ids.** Two sensations with the same id in one game collide.
- **Send-by-id needs registration.** A baked send transmits only the id; the app can
  only play it if it received that registration in your AUTH. If in doubt, send the
  full sensation (its `reference`) as a live sensation instead.
- **Reference is a real sensation.** Anything you can build with the SDK works as the
  reference, including sequences and ramps.
- **Keep separators out of names.** `~ # | & , %` are structural.
