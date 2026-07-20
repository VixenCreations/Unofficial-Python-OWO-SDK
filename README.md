# Unofficial Python OWO SDK

> **AI assistance notice:** this SDK was developed with the assistance of
> **Claude Code (Opus 4.8)**. The protocol reimplementation, documentation, and test
> tooling were produced with AI pair-programming, then verified byte-for-byte against
> the wire format and tested on real OWO hardware.

A small, dependency-free Python library for driving an [OWO](https://owogame.com)
haptic suit. It talks to the OWO desktop app directly over UDP using only the
standard library, so there is no `pythonnet`, no `OWO.dll`, and no .NET runtime to
install.

> Unofficial project. Not affiliated with, endorsed by, or sponsored by OWO. It is an
> independent reimplementation of the OWO SDK's wire protocol. Tested against real
> game ids and hardware.

## Why

The official OWO SDK is .NET only, which forces a `pythonnet` + `OWO.dll` + .NET
runtime stack into any Python project that wants to use it. This library replaces all
of that with a single file, `owo.py`, and the same connect / send / stop surface.

## Requirements

- Python 3.9+
- The OWO desktop app running, with the suit powered on and connected in the app

No pip installs. Drop `owo.py` next to your script, or add this folder to your path.

## Quick start

```python
import owo
from owo import OWOClient, Muscle, sensation

client = OWOClient(game_id="0")   # "0" is an anonymous connection
client.open()

if client.auto_connect(timeout=15):
    client.send(sensation(frequency=80, duration_s=0.5, intensity=60),
                Muscle.PECTORAL_R, Muscle.PECTORAL_L)

client.stop()
client.close()
```

Play a saved OWO pattern file:

```python
client.send(owo.parse(open("samples/full_body.owo").read().strip()))
```

Hold a continuous effect and change its strength live:

```python
handle = client.sustain(sensation(60, 0.4, 100), *owo.muscles_front())
handle.set_intensity(75)   # 0-100, adjust any time
handle.stop()
```

## What it does

- Discovery and the full `ping` / `okay` / `pong` handshake, with keepalive and
  automatic reconnect.
- Build sensations in code (`sensation(...)`), target any of the 10 muscles, compose
  sequences, ramp, and scale intensity.
- Read and write OWO `.owo` pattern files (`parse` / `serialize`), byte-compatible
  with the OWO Sensations Creator.
- Sustained (looping) drive for effects that track a live value.
- Registered "baked" sensations so the app resolves them by name and icon.

## Documentation

- [Getting started](Documentation/GETTING_STARTED.md) - the friendly quick tour.
- [Developer guide](Documentation/DEVELOPER_GUIDE.md) - the full API and patterns.
- [Baked sensations guide](Documentation/BAKED_SENSATIONS_GUIDE.md) - authoring and
  loading `.owoauth` files.
- [Wire protocol](Reverse%20Engineering/OWO_PROTOCOL.md) - the reverse-engineered
  protocol reference.

## Try it

`example.py` is a minimal demo. `test_rig.py` is a fuller smoke test with three modes:

```
py -3.11 test_rig.py --dry          # print the exact wire strings, no hardware
py -3.11 test_rig.py --visualizer   # fire into the OWO Visualizer (no suit)
py -3.11 test_rig.py                # connect to the OWO app and fire on the suit
```

Sample patterns live in `samples/`.

## License

MIT. See [LICENSE](LICENSE), which also carries the attribution for the original
MIT-licensed OWO SDK this reimplements.
