# OWO Python SDK: developer guide

A dependency-free Python implementation of the OWO game SDK. It talks to the OWO
desktop app over UDP using only the standard library, replacing the pythonnet +
`OWO.dll` + .NET stack. This guide covers the full API and the patterns you need to
build a robust integration.

- Single file: `owo.py`. Import it directly, or vendor it into your package.
- Requires Python 3.9+. No third-party dependencies.
- The wire-protocol reference is `OWO_PROTOCOL.md` (internal deep-dive).

```python
import owo
from owo import OWOClient, Muscle, sensation, serialize, parse
```

Everything intended for public use is in `owo.__all__`.

## 1. Connection lifecycle

```python
client = OWOClient(game_id="0", registered_sensations=[])
client.open()                      # opens the socket + starts the receive thread
client.auto_connect(timeout=15)    # blocks until connected (pong) or timeout
...
client.close()                     # stops threads, closes the socket
```

- `open()` creates one unbound UDP socket and starts a background receive thread. Call
  it once. `close()` tears everything down (and stops any active sustains).
- `auto_connect(timeout=None, block=True)` discovers apps by broadcast. `connect(*ips,
  timeout=None, block=True)` targets specific hosts (use this when the app is on
  another machine or when broadcast is blocked). Both return `True` once connected.
  With `block=False` they return immediately and you poll `is_connected`.
- State is exposed read-only: `is_connected` (bool), `state` (`"Disconnected"` /
  `"Connecting"` / `"Connected"`), `connected_servers` (list of IPs), `discovered_apps`.

### Threading model

`open()` starts a receive thread and a scan/keepalive thread; both are daemons. All
public methods are safe to call from your own thread. Your `send`/`sustain` calls
hand work to those threads or send immediately from the caller. Do not share one
`OWOClient` socket across processes.

### Keepalive and reconnect

While connected, the scan thread pings each connected server every `scan_interval`
(default 0.5s) and drops any server that has not answered within `keepalive_timeout`
(default 3.0s), then resumes discovery. This catches silent drops (sleep/wake, cable
pull) that never send an explicit close. Clean shutdowns from the app arrive as
`OWO_Close` and are handled the same way.

Tuning (constructor args):

| Arg | Default | Effect |
|---|---|---|
| `scan_interval` | `0.5` | ping cadence for discovery and keepalive |
| `keepalive` | `True` | set `False` to disable the liveness check |
| `keepalive_timeout` | `3.0` | seconds of silence before a server is dropped |
| `auto_reconnect` | `True` | re-discover automatically after a drop |

`auto_reconnect=True` means a dropped connection quietly comes back on its own; your
`send` calls are no-ops while disconnected and resume when the link returns.

## 2. Building sensations

A sensation is an immutable value object. Build the atom with `sensation(...)`:

```python
s = sensation(frequency=80, duration_s=0.5, intensity=60,
              ramp_up_s=0.1, ramp_down_s=0.1, exit_delay_s=0.0, name="")
```

| Field | Range | Notes |
|---|---|---|
| `frequency` | 1-100 Hz | motor frequency |
| `duration_s` | 0.1-20 s | play time (rounded to 0.1) |
| `intensity` | 0-100 | strength |
| `ramp_up_s` / `ramp_down_s` | 0-2 s | fade in / out |
| `exit_delay_s` | 0-20 s | trailing gap; `duration` = `duration_s + exit_delay_s` |
| `name` | string | optional label |

Values are clamped to their ranges on construction, so out-of-range input is safe.

Target muscles and compose:

```python
s.with_muscles(Muscle.PECTORAL_R, Muscle.PECTORAL_L)  # bind to muscles
s.multiply_intensity(50)                              # scale to 50% (returns a copy)
a.append(b)                                           # play a then b (a sequence)
s.with_priority(5)                                    # see priority below
```

Muscles: ids 0-9, `Muscle.PECTORAL_R/L`, `ABDOMINAL_R/L`, `ARM_R/L` (front),
`DORSAL_R/L`, `LUMBAR_R/L` (back). Each carries its own 0-100 intensity:
`Muscle.ARM_L.with_intensity(70)`. Helpers: `muscles_front()`, `muscles_back()`,
`muscles_all()`, `mirror_muscles(list)`, and `m.mirror()` (swap left/right).

Sensation types you may receive from `parse`: `MicroSensation` (the atom),
`SensationWithMuscles`, `SensationsSequence`, `BakedSensation`. All expose
`.duration` (a property, seconds) and `.multiply_intensity(pct)`.

## 3. Sending

```python
client.send(a_sensation, *muscles, priority=None, force=False)
client.stop()   # stop current output on all connected servers
```

- `send` returns `True` if a frame went out, `False` if not connected or suppressed
  by priority.
- Passing muscles here is shorthand for `.with_muscles(...)`.
- **Priority gating:** a new sensation of equal or higher priority plays immediately;
  a lower-priority one is suppressed until the current sensation's duration elapses.
  Pass `force=True` to bypass this and always send. `stop()` resets the gate.
- **One sensation at a time:** sending anything ends whatever is currently playing.
  For a held effect, use sustain (below), not a manual loop.

## 4. Sustained (continuous) drive

`sustain` keeps one sensation playing by re-sending it just before it elapses, and
lets you change its strength live. This is the right tool for tracking a live
0-100 value (a touch level, a mic level, a distance):

```python
handle = client.sustain(sensation(60, 0.4, 100), *owo.muscles_front(), priority=None)

handle.set_intensity(level)     # 0-100, derived from the ORIGINAL each call (no compounding)
handle.update(other_sensation)  # swap the whole sensation, keep looping
handle.running                  # bool
handle.stop()                   # end it and stop the suit
```

Notes:
- `set_intensity` always scales the base sensation, so calling it every frame is safe
  and will not drift.
- Sustain survives reconnects: while disconnected the internal send is a no-op and it
  resumes automatically.
- `client.stop()` and `client.close()` stop all active sustains. A per-handle
  `handle.stop()` stops only that one (and issues a device stop).
- `lead_s` (default 0.05) is how early each repeat is sent so there is no audible gap;
  raise it slightly if you hear stutter, lower it to reduce traffic.

## 5. Registered (baked) sensations

Registering sensations under your OWO game id lets the app resolve them by id and show
their names, icons, and families. Load an exported `.owoauth` file:

```python
regs = owo.load_owoauth("my_game.owoauth")
client = OWOClient(game_id="<your-game-id>", registered_sensations=regs)
```

- `load_owoauth(path)` returns a list of `BakedSensation`. The file is one or more
  registration strings (`id~name~reference~icon~family`) joined by `#`.
- The list is serialized into the AUTH handshake via `build_game_auth(sensations)`.
- Use your real game id to get named connections and resolvable baked sensations.
  Game id `"0"` is anonymous: live sensations still work, but registered names/icons
  do not resolve.
- Icons are the `Icon.*` constants (e.g. `Icon.IMPACT_BULLET`, `Icon.WEAPON_DAGGER`);
  families are free-form grouping strings.

You do not need registered sensations to send live ones. Sending a full wire sensation
(what `sensation(...)` and `parse(".owo")` produce) works with an empty auth.

To author new `.owoauth` sensations (the file format, a fill-in template, the OWO
Sensations Creator export, or building them in code with `save_owoauth`), see
`BAKED_SENSATIONS_GUIDE.md`.

## 6. Parsing and serializing

```python
wire = serialize(a_sensation)     # sensation -> wire text
s = parse(wire_or_owo_file_text)  # wire text / .owo content -> sensation
```

`parse` and `serialize` round-trip losslessly and are byte-compatible with `.owo`
files and the OWO Sensations Creator. Wire format (all ASCII):

- Micro-sensation: `frequency,duration_ds,intensity,rampUp_ms,rampDown_ms,exitDelay_ds,name`
  where `_ds` is deciseconds (x10) and `_ms` is milliseconds (x1000).
- With muscles: `<micro>|id%intensity,id%intensity,...`
- Sequence: `<sensation>&<sensation>&...`
- Baked (when sent): just the numeric id. Registration form: `id~name~reference~icon~family`.

Parse precedence is `~` (baked) then `&` (sequence) then `|` (muscles) then micro, so
a sequence of muscle-targeted sensations parses correctly.

## 7. Presets

Ready-made sensations mirror the OWO built-ins: `owo.ball()`, `dart()`,
`dagger_entry()`, `dagger_movement()`, `dagger()`, `shot_entry()`, `shot_exit()`,
`shot_bleeding()`, `shot_with_exit()`. Compose them like any sensation:
`owo.dagger().with_muscles(Muscle.ABDOMINAL_R)`.

## 8. Sensors and battery

The suit protocol is output-only in this SDK; there is no battery or sensor read. If
you need those, read them from the OWO app itself.

## 9. Error handling

- Construction clamps values; it does not raise on out-of-range numbers.
- `send`/`sustain` never raise on a dead connection; they return `False` / no-op and
  recover on reconnect. Wrap your own file and parse calls (`load_owoauth`, reading
  `.owo`) in try/except for bad input.
- Discovery/keepalive failures are logged through the `owo` logger, not raised. Enable
  them with `logging.getLogger("owo").setLevel(logging.DEBUG)`.

## 10. Quick reference

Connection: `OWOClient(game_id, registered_sensations, port, scan_interval,
auto_reconnect, keepalive, keepalive_timeout)`, `open`, `auto_connect`, `connect`,
`disconnect`, `close`, `is_connected`, `state`, `connected_servers`, `discovered_apps`.

Output: `send(sensation, *muscles, priority, force)`, `stop`, `sustain(sensation,
*muscles, priority, lead_s) -> SustainedPlayback`.

`SustainedPlayback`: `set_intensity(pct)`, `update(sensation)`, `stop()`, `running`.

Build: `sensation(...)`, `Muscle`, `muscles_front/back/all`, `mirror_muscles`, `Icon`,
`ball/dart/dagger*/shot*`, `with_muscles`, `with_intensity`, `multiply_intensity`,
`with_priority`, `append`.

Wire: `serialize`, `parse`, `parse_muscles`, `build_game_auth`, `load_owoauth`.

See `test_rig.py` for a working end-to-end example (dry / visualizer / live modes,
registered-sensation loading, and a sustained-drive demo).
