# OWO Suit Wire Protocol (reverse-engineered)

A protocol reference for the OWO game SDK network protocol, recovered by decompiling
the official `OWO.dll` (NuGet package `OWO`, version 2.4.2, MIT licensed) with ILSpy,
plus a functional cross-check of the round-tripped serialization in `owo.py`.

This is the authoritative "why" behind `owo.py`. The Python module is a
byte-for-byte reimplementation of what follows, with zero pythonnet and zero
.NET runtime dependency.

## 1. Source of truth

- Package: `OWO` 2.4.2, target frameworks `net48` and `net6.0` (identical API).
- Namespace: `OWOGame`. License: MIT (free to reimplement).
- Decompiler build stamp in the PDB path: `api-net-core/OWOPlugin` by "Lino".
- The public facade is the static class `OWOGame.OWO`; everything else is the
  transport, discovery and serialization machinery it drives.

## 2. Transport

- Protocol: UDP, IPv4.
- Target port: `54020` (constant `UDPNetwork.PORT`, runtime-overridable via
  `OWO.Instance.PortTo`).
- One `Socket(InterNetwork, Dgram, Udp)` per client, `EnableBroadcast = true`,
  `ReceiveTimeout = 2500 ms`, non-blocking.
- The socket is never bound to a local port. It sends from an OS-assigned
  ephemeral port and receives replies on that same ephemeral port. Consequence:
  the OWO app replies to the source address+port of the packet it received, not
  to a fixed port. A client therefore must use a single socket for both send and
  receive (as `owo.py` does), and must not bind `54020` locally (that port
  belongs to the OWO app, which may be on the same machine).
- Encoding: ASCII. Every message is a plain ASCII string, no length prefix, no
  framing, one logical message per datagram. Receive buffer is 1024 bytes.

## 3. Message verbs

All payloads are ASCII text. `{id}` is the game id string from `GameAuth`
(default `"0"`). `{auth}` is the serialized game auth (section 6). `{sensation}`
is a serialized sensation (section 5).

### Client to app

| Message | Meaning | Sent by |
|---|---|---|
| `ping` | Presence / discovery probe | `FindServer.NotifyPresence` |
| `{id}*AUTH*{auth}` | Authenticate this game with the app | `Connect.AutoConnect` / `ManualConnect` |
| `{id}*SENSATION*{sensation}` | Play a sensation now | `SendSensation.Execute` |
| `{id}*STOP` | Stop the current sensation | `StopSensation.Execute` |
| `{prefix}*GAMEUNAVAILABLE` | This game is going away; `{prefix}` = `auth.Split('*')[0]` = `{id}` | `NotifyAbscense.Execute` |

### App to client

| Message | Meaning |
|---|---|
| `okay` | App is present and accepting a game (reply to `ping`) |
| `pong` | Auth accepted; connection verified (reply to `AUTH`) |
| `OWO_Close` | App / suit is disconnecting |

The verb separator is `*`. `AUTH` and `SENSATION` carry a payload after a second
`*`; `STOP` and `GAMEUNAVAILABLE` do not.

## 4. Handshake and connection lifecycle

Discovery (`FindServer.Scan`, used by `StartScan` / the auto path):

1. Broadcast `ping` to `255.255.255.255:54020`.
2. On `okay` from an app, store that app's IP as a candidate.
3. Repeat every `scanDelayMs` (default 500 ms) while `Disconnected`.

Connect (`FindServer.Execute`, used by `AutoConnect` and `Connect(ips)`):

1. State becomes `Connecting`.
2. For each target already known as a candidate, send `{id}*AUTH*{auth}`.
3. Loop every 500 ms:
   - Listen for a message.
   - If nothing arrived, re-`ping` every target.
   - If `okay`, store the sender as a candidate and send it `{id}*AUTH*{auth}`.
   - If `pong` and the sender is one of the targets (or the target was the
     broadcast address), mark that sender connected.
   - Continue until every target is connected, or state becomes `Disconnected`.
4. After connecting, `AutoConnect` also emits one `GAMEUNAVAILABLE` cleanup pass
   through `NotifyAbscense` against stale candidates.

Auto address is the literal broadcast string `255.255.255.255`; passing it
resets the candidate vault first.

Disconnect watch (`ListenForDisconnection.Listen`): while `Connected`, listen;
on `OWO_Close` from a connected server, drop it and (in the SDK) re-enter
`FindServer` for that address. `owo.py` mirrors this: `OWO_Close` clears the
server and, if `auto_reconnect`, resumes scanning.

Practical minimal client flow (what `owo.py` implements):

```
broadcast "ping"            (repeat every 0.5s while not connected)
recv "okay"   from APP_IP   -> send "{id}*AUTH*{auth}" to APP_IP
recv "pong"   from APP_IP   -> state = Connected, remember APP_IP
send "{id}*SENSATION*..."   to each connected APP_IP
send "{id}*STOP"            to each connected APP_IP
recv "OWO_Close"            -> drop APP_IP, resume discovery
```

## 5. Sensation serialization

A sensation implicitly converts to a string via `SensationsBuilder.From`. Four
concrete shapes exist; each has an exact text form and an exact parser
(`SensationsParser`), so the format round-trips.

### 5.1 Micro-sensation (the atom)

Fields and clamps (`MicroSensation` constructor):

| Field | Range / clamp | Stored as | Serialized as |
|---|---|---|---|
| frequency | int 1..100 (Hz) | int | `frequency` |
| duration | float 0.1..20 s, rounded to 0.1 | seconds | `round(duration * 10)` deciseconds |
| intensity | int 0..100 (%) | int | `intensity` |
| rampUp | float 0..2 s, rounded to 0.1 | seconds | `round(rampUp * 1000)` milliseconds |
| rampDown | float 0..2 s, rounded to 0.1 | seconds | `round(rampDown * 1000)` milliseconds |
| exitDelay | float 0..20 s, rounded to 0.1 | seconds | `round(exitDelay * 10)` deciseconds |
| name | string (optional) | string | `name` (may be empty) |

Wire form (exactly seven comma-separated fields, trailing name always present):

```
frequency,duration_ds,intensity,rampUp_ms,rampDown_ms,exitDelay_ds,name
```

Example: `sensation(80, 0.5, 60, 0.3, 0.1, 0.2)` serializes to `80,5,60,300,100,2,`.

Note the unit inconsistency baked into the format: duration and exit-delay are
deciseconds (x10), but ramp-up and ramp-down are milliseconds (x1000). This is
faithfully reproduced. Also note the public factory parameter names
`rampUpMillis` / `rampDownMillis` are misleading: the values are clamped to
0..2 and treated as seconds, then multiplied by 1000. In `owo.py` the parameters
are named by their true unit (`ramp_up_s`, `ramp_down_s`).

Parser (`MicrosensationsParser`): split on `,`; `duration = f[1]/10`,
`rampUp = f[3]/1000`, `rampDown = f[4]/1000`, `exitDelay = f[5]/10`,
`name = f[6]` if present.

`Duration` (used for send timing) is `duration + exitDelay` seconds.

#### 5.1.1 Cross-check against the OWO Sensations Creator UI

Confirmed against the official OWO **Sensations Creator** (v2.9.14) loading a sample
template. `full_body.owo` on disk is:

```
52,50,65,0,0,0,|0%80,1%80,2%80,3%80,4%80,5%80,6%80,7%80,8%80,9%80
```

and the Creator renders its single micro-sensation as:

| Wire field | Value | Creator control | Creator shows | Unit relationship |
|---|---|---|---|---|
| `frequency` | 52 | Frequency [Hz] | 52 Hz | 1:1 |
| `duration_ds` | 50 | Duration [s] | 05.00 | value / 10 (deciseconds) |
| `intensity` | 65 | Intensity [%] | 65 % | 1:1 |
| `rampUp_ms` | 0 | Fade In [ms] | 0 | 1:1 (milliseconds) |
| `rampDown_ms` | 0 | Fade Out [ms] | 0 | 1:1 (milliseconds) |
| `exitDelay_ds` | 0 | Exit Time [s] | 00.00 | value / 10 (deciseconds) |
| `name` | (empty) | Enter name... | (blank) | 1:1 |
| muscle list `|id%int` | all `%80` | body diagram | every pad shows 80 | per-muscle intensity |

This is exactly the interpretation `owo.py` implements (`parse`: `parts[1]/10`,
`parts[3]/1000`, `parts[4]/1000`, `parts[5]/10`; `serialize` inverts it). The
Creator's **Export with muscles** toggle being on is why the wire form carries the
trailing `|muscles`; **Bake** off is why it exports as a raw sensation rather than a
numeric baked id. Cross-checked fade example: `heartbeat.owo` field 3 = `100` shows
as Fade In 100 ms (`owo.py`: `100/1000 = 0.1 s`, re-serialized `round(0.1*1000) = 100`).

**Round-trip fidelity is proven,** not assumed: `test_rig.py --dry` parses the sample
templates (`full_body/heartbeat/front_to_back`) and re-serializes each
**byte-for-byte identical** to the on-disk file, so the pure-Python SDK is a faithful
match to both the `.owo` format and the Creator's own encoding. (The parser splits
sequences `&` and baked `~` before the muscle separator `|`, or a
sequence-of-muscle-sensations would mis-parse.)

### 5.2 Sensation with muscles

```
{micro-sensation}|{muscle},{muscle},...
```

where each muscle is `{id}%{intensity}` (`MusclesBuilder.From`). Separator
between the sensation and its muscle list is `|`; muscles are comma-separated.
Example: `80,5,60,300,100,2,|0%100,1%100`.

Parser: `SensationWithMusclesParser` splits on `|`; `MusclesParser` splits the
right side on `,` then each token on `%`.

### 5.3 Sequence

```
{sensation}&{sensation}&...
```

Sensations played back to back (`SensationsBuilder.From(SensationsSequence)`,
separator `&`). `Duration` is the sum. Parser: `SequenceParser` splits on `&`.

### 5.4 Baked sensation

A sensation pre-registered with the app under a numeric id. When sent, it
serializes to just its id (`BakedSensationsBuilder.From`), so the wire form of a
baked sensation is literally `{id}` (or `{id}|{muscles}` when re-targeted). The
app looks up the referenced sensation from the game auth it received.

Its registration form (used only inside the AUTH payload, section 6) uses `~`:

```
{id}~{name}~{reference-sensation}~{icon}~{family}
```

Parser precedence (`SensationsParser.From`): a message parses as baked if it has
no `,` and no `|`, or if it contains `~`; else as sequence if it has `&`; else
as sensation-with-muscles if it has `|`; else as a micro-sensation.

## 6. Game auth payload

`GameAuth` has an `id` string (default `"0"`) and an array of baked sensations
(default empty). It serializes (`GamesBuilder.Build`) to the baked sensations'
registration strings joined by `#\n`, or the empty string when there are none.

The AUTH message is therefore `{id}*AUTH*{joined-baked-sensations}`. For a plain
connection with no pre-registered sensations this is `{id}*AUTH*` with an empty
tail. The id is the developer's registered OWO game id; using your real id lets
the app resolve custom sensations, names, icons and families. `"0"` works for an
anonymous connection that only sends live (non-baked) sensations.

Parser (`GamesParser.From`): split on `#`; a pure integer is treated as an id;
otherwise each segment parses as a baked sensation.

## 7. Muscles

Ten muscles, ids 0..9:

| id | name | id | name |
|---|---|---|---|
| 0 | Pectoral_R | 1 | Pectoral_L |
| 2 | Abdominal_R | 3 | Abdominal_L |
| 4 | Arm_R | 5 | Arm_L |
| 6 | Dorsal_R | 7 | Dorsal_L |
| 8 | Lumbar_R | 9 | Lumbar_L |

Groups: Front = 0,1,2,3,4,5; Back = 6,7,8,9; All = Front + Back. Mirror maps a
muscle to its left/right pair: even id -> id+1, odd id -> id-1. Each muscle
carries an intensity 0..100 that scales that muscle's share of the sensation.

## 8. Intensity multiplier

`Multiplier` is an int percentage. Muscle scaling
(`Muscle.MultiplyIntensityBy`) computes `intensity * percentage / 100` then
clamps 0..100. `owo.py` applies that same percentage scaling to muscles and to
micro-sensation intensity.

DLL quirk worth knowing: `MicroSensation.MultiplyIntensityBy` in the original
multiplies raw (`intensity * percentage`, no divide by 100) before clamping,
because of a C# operand-order overload-resolution accident. That is almost
certainly an OWO bug; `owo.py` deliberately uses the sane percentage semantics
for both muscles and sensations. This only matters if you call
`multiply_intensity` on a sensation; it never affects normally authored
sensations.

## 9. Send timing and priority

`SendSensation.Execute(sensation, nowMs)` gates sends:

```
if lastPriority <= sensation.Priority or nowMs >= whenLastSensationEnds:
    send "{id}*SENSATION*{sensation}"
    whenLastSensationEnds = nowMs + int(sensation.Duration * 1000)
    lastPriority = sensation.Priority
```

So a new sensation of equal or higher priority always plays immediately; a
lower-priority one is suppressed until the current sensation's duration elapses.
`Stop` / `ResetPriority` zero the timer. `owo.py` reproduces this via
`_priority_allows`, with a `force=True` escape hatch on `send`.

Important behavioral note from OWO's own docs: sending any sensation ends
whatever is currently playing. For sustained effects you must re-send on a loop.

## 10. Built-in presets and icons

Presets (static members of `Sensation`), reproduced in `owo.py`:

| Preset | Definition |
|---|---|
| Ball | `Create(100, 0.1)` |
| Dart | `Create(10, 0.1)` |
| DaggerEntry | `Create(60, 0.2)` |
| DaggerMovement | `Create(100, 2, 100, 0.3, 0.1)` |
| Dagger | `DaggerEntry.Append(DaggerMovement)` |
| ShotEntry | `Create(30, 0.1)` on Pectoral_R |
| ShotExit | `Create(20, 0.1)` on Dorsal_R |
| ShotBleeding | `Create(50, 0.5, 80, 0, 0.3)` on Pectoral_R + Pectoral_L |
| ShotWithExit | `ShotEntry.Append(ShotExit).Append(ShotBleeding)` |

Icons (used only for baked/registered sensations): `Impact-0..3`
(Ball / Dart / Punch / Bullet), `Weapon-0..3`
(Axe / Dagger / Gun / SubMachineGun), `Death-0`, `Spider-0`, `Weight-0`,
`Environment-0`, `Alert-0`, `Victory-0`, and `0` (empty). Families are free-form
strings that group sensations in the app.

## 11. What this replaces

Integrations that talk to an OWO suit typically load `OWO.dll` through pythonnet
(`Assembly.LoadFrom`, `from OWOGame import OWO, ...`) and hard-fail at import if the
DLL is missing. `owo.py` makes that entire dependency chain (the DLL, the `clr` /
pythonnet package, the bundled .NET runtime) unnecessary: the same connect / send /
stop surface is available in pure Python, implementing the protocol directly
(broadcast `ping` / `okay` / `pong` handshake plus the text sensation format).
