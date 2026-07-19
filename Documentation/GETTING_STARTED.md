# OWO Python SDK: getting started

A small Python library that talks to your OWO suit directly. No .NET, no `OWO.dll`,
no extra installs beyond Python itself. Connect, send a sensation, done.

## What you need

- The **OWO desktop app** running, with your suit powered on and connected in the app.
- **Python 3.9+**.
- `owo.py` on hand (this folder). Put your script next to it, or add this folder to
  your path.

## Fire your first sensation

```python
import owo
from owo import OWOClient, Muscle, sensation

client = OWOClient(game_id="0")   # "0" is an anonymous connection
client.open()

if client.auto_connect(timeout=15):
    print("Connected to", client.connected_servers)
    client.send(sensation(frequency=80, duration_s=0.5, intensity=60),
                Muscle.PECTORAL_R, Muscle.PECTORAL_L)
else:
    print("No OWO app found. Is it running with the suit on?")

client.stop()
client.close()
```

`sensation(...)` builds one pulse: how fast (`frequency`, 1-100 Hz), how long
(`duration_s`), how strong (`intensity`, 0-100). The muscles after it are where you
feel it.

## Play a saved pattern

OWO pattern files (`.owo`) drop straight in:

```python
content = open("Axe.owo", encoding="utf-8").read().strip()
client.send(owo.parse(content))
```

## Hold an effect

A single `send` plays once. To keep something going (and change its strength as it
plays), use `sustain`:

```python
handle = client.sustain(sensation(60, 0.4, 100), *owo.muscles_front())
handle.set_intensity(75)   # adjust any time, 0-100
...
handle.stop()              # stop when you are done
```

## The muscles

Ten spots, left and right: `PECTORAL`, `ABDOMINAL`, `ARM` (front), `DORSAL`,
`LUMBAR` (back). Use `Muscle.PECTORAL_R`, `Muscle.ARM_L`, and so on, or the groups
`owo.muscles_front()`, `owo.muscles_back()`, `owo.muscles_all()`.

## If something is off

- **It never connects.** Make sure the OWO app is open and the suit shows as
  connected there. If your app is on another machine, pass its address:
  `client.connect("192.168.1.50")`.
- **It connects but you feel nothing.** Check the suit is on and worn, and that the
  intensity is not 0. Try a full-body test: `client.send(sensation(60, 1.0, 100),
  *owo.muscles_all())`.
- **Only one app at a time.** The OWO app and the OWO Visualizer both use the same
  port, so run just one of them while you test.

That is the whole surface for everyday use. When you want the full API, patterns for
live-tracking effects, registered sensations, and reconnection behavior, see
`DEVELOPER_GUIDE.md`.
