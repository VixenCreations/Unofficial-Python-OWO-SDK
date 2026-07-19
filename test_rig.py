"""OWO SDK smoke-test rig.

Exercises the pure-Python OWO SDK (owo.py) end to end using only the standard
library. Three checks:

  1. connect      - discover + ping/okay/pong handshake to a running OWO endpoint.
  2. programmatic - fire sensations built in code via owo.sensation(...).
  3. baked        - fire the sample .owo template files, parsed by owo.parse().

Three run modes:

    py -3.11 test_rig.py --dry            Offline: print exact wire strings, no network.
    py -3.11 test_rig.py --visualizer     Connect to the OWO Visualizer (no suit) and fire.
    py -3.11 test_rig.py                   Live: connect to the OWO app and fire on the suit.

--visualizer and live use the real handshake and WAIT for the connection (pong)
before firing. The default game id is anonymous "0"; pass --game-id <your id> to
connect as a registered game so baked names and icons resolve. Use --target IP if
broadcast discovery does not find the endpoint (e.g. --target 127.0.0.1 for a
same-machine Visualizer).
"""

import argparse
import logging
import os
import sys
import time

import owo
from owo import Muscle, OWOClient, muscles_front, parse, sensation, serialize

log = logging.getLogger("owo_test_rig")

SAMPLES_DIR = os.path.join(os.path.dirname(__file__), "samples")
DEFAULT_OWOAUTH = os.path.join(SAMPLES_DIR, "example.owoauth")
DEFAULT_BAKED = ["full_body", "heartbeat", "front_to_back"]
BROADCAST = "255.255.255.255"


def _load_registered(path):
    """Load registered baked sensations so the app resolves their names and icons."""
    if not path or not os.path.isfile(path):
        return []
    try:
        regs = owo.load_owoauth(path)
        log.info("loaded %d registered baked sensation(s) from %s", len(regs), path)
        return regs
    except Exception as exc:
        log.warning("could not load .owoauth (%s): %s", path, exc)
        return []


def _wire(a_sensation):
    return f"0*SENSATION*{serialize(a_sensation)}"


def _programmatic_demos():
    """The sensations built in code to fire, as {label: sensation}."""
    return {
        "single pulse (chest R)":
            sensation(frequency=80, duration_s=0.5, intensity=60).with_muscles(Muscle.PECTORAL_R),
        "ramped heartbeat (both chest)":
            sensation(frequency=40, duration_s=0.3, intensity=80, ramp_up_s=0.1, ramp_down_s=0.1)
            .with_muscles(Muscle.PECTORAL_R, Muscle.PECTORAL_L),
        "front sweep at 50 percent":
            sensation(60, 1.0, 100).with_muscles(*muscles_front()).multiply_intensity(50),
        "dagger preset (torso)":
            owo.dagger().with_muscles(Muscle.ABDOMINAL_R),
    }


def _load_templates(names, template_dir):
    """Return [(name, content, parsed_sensation)] for the given template stems."""
    out = []
    for name in names:
        path = os.path.join(template_dir, f"{name}.owo")
        if not os.path.isfile(path):
            log.warning("template not found, skipping: %s", path)
            continue
        with open(path, "r", encoding="utf-8") as fh:
            content = fh.read().strip()
        try:
            parsed = parse(content)
        except Exception as exc:
            log.error("parse failed for %s: %s", name, exc)
            out.append((name, content, None))
            continue
        out.append((name, content, parsed))
    return out


def prove_programmatic(client, dry):
    ok = True
    for label, s in _programmatic_demos().items():
        if dry:
            print(f"  {label:40s} -> {_wire(s)}")
            continue
        try:
            sent = client.send(s, force=True)
            print(f"  {'fired' if sent else 'DROPPED (not connected)'}: {label}")
            ok = ok and sent
            time.sleep(s.duration + 0.3)
        except Exception as exc:
            log.error("send failed (%s): %s", label, exc)
            ok = False
    return ok


def prove_baked(client, dry, names, template_dir):
    loaded = _load_templates(names, template_dir)
    if not loaded:
        log.error("no baked templates loaded from %s", template_dir)
        return False
    ok = True
    for name, content, parsed in loaded:
        if parsed is None:
            ok = False
            continue
        if dry:
            print(f"  {name:16s} file  = {content}")
            print(f"  {name:16s} wire  = {_wire(parsed)}")
            print(f"  {name:16s} secs  = {parsed.duration:.1f}")
            continue
        try:
            sent = client.send(parsed, force=True)
            print(f"  {'fired baked' if sent else 'DROPPED'}: {name} (~{parsed.duration:.1f}s)")
            ok = ok and sent
            time.sleep(parsed.duration + 0.5)
        except Exception as exc:
            log.error("baked send failed (%s): %s", name, exc)
            ok = False
    return ok


def prove_sustain(client, secs):
    """Hold one continuous sensation via sustain(), ramping intensity live, then stop."""
    base = sensation(60, 0.4, 100).with_muscles(*muscles_front())
    print(f"  sustaining front sweep for {secs:.0f}s, ramping intensity 20 -> 100...")
    handle = client.sustain(base)
    t0 = time.time()
    try:
        while time.time() - t0 < secs:
            frac = (time.time() - t0) / secs
            handle.set_intensity(int(20 + 80 * frac))
            time.sleep(0.25)
    finally:
        handle.stop()
    print("  sustain stopped (released)")
    return not handle.running


def _connect(mode, game_id, timeout, target, owoauth, results):
    """Open an OWOClient and WAIT for the handshake. Returns the client or None."""
    client = OWOClient(game_id=game_id, registered_sensations=_load_registered(owoauth))
    client.open()
    where = "Visualizer/app" if mode == "visualizer" else "OWO app"
    if target and target != BROADCAST:
        print(f"Connecting to {where} at {target} (waiting up to {timeout}s for pong)...")
        connected = client.connect(target, timeout=timeout)
    else:
        print(f"Discovering {where} via broadcast (waiting up to {timeout}s for pong)...")
        connected = client.auto_connect(timeout=timeout)
    results["connect"] = connected
    if not connected:
        print(f"FAIL connect: no OWO endpoint responded. Is the {where} running "
              f"(and the suit on, for live)?")
        client.close()
        return None
    print(f"PASS connect: {client.connected_servers}")
    return client


def run(mode, game_id, timeout, names, template_dir, target, owoauth, sustain_s):
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    results = {}
    dry = mode == "dry"

    client = None
    if not dry:
        client = _connect(mode, game_id, timeout, target, owoauth, results)
        if client is None:
            return _summary(results)

    print("\n[programmatic sensations]")
    results["programmatic"] = prove_programmatic(client, dry)

    print("\n[baked templates]")
    results["baked"] = prove_baked(client, dry, names, template_dir)

    if client is not None and sustain_s > 0:
        print("\n[sustained drive]")
        results["sustain"] = prove_sustain(client, sustain_s)

    if client is not None:
        try:
            client.stop()
        finally:
            client.close()

    return _summary(results)


def _summary(results):
    print("\n=== SMOKE TEST SUMMARY ===")
    all_ok = True
    for key in ("connect", "programmatic", "baked", "sustain"):
        if key not in results:
            continue
        state = "PASS" if results[key] else "FAIL"
        all_ok = all_ok and results[key]
        print(f"  {state}  {key}")
    print("==========================")
    return 0 if all_ok else 1


def main(argv=None):
    ap = argparse.ArgumentParser(description="OWO pure-Python SDK smoke-test rig")
    mode = ap.add_mutually_exclusive_group()
    mode.add_argument("--dry", action="store_true", help="offline: print wire strings, no network")
    mode.add_argument("--visualizer", action="store_true",
                      help="connect to the OWO Visualizer (no suit) and fire")
    ap.add_argument("--target", default=BROADCAST,
                    help="specific endpoint IP (use 127.0.0.1 if broadcast does not find it)")
    ap.add_argument("--game-id", default="0",
                    help='OWO game id (default anonymous "0"; pass your registered id to resolve baked names)')
    ap.add_argument("--timeout", type=float, default=15.0, help="connect timeout seconds")
    ap.add_argument("--template", action="append", dest="templates",
                    help="baked template stem to test (repeatable); default: the samples")
    ap.add_argument("--template-dir", default=SAMPLES_DIR,
                    help="directory holding *.owo files")
    ap.add_argument("--owoauth", default=DEFAULT_OWOAUTH,
                    help="registered-sensations .owoauth file (empty string to skip)")
    ap.add_argument("--sustain", type=float, default=0.0, metavar="SECS",
                    help="also run a sustained-drive demo for SECS (live/visualizer only)")
    args = ap.parse_args(argv)

    selected = "dry" if args.dry else "visualizer" if args.visualizer else "live"
    names = args.templates or DEFAULT_BAKED
    return run(selected, args.game_id, args.timeout, names, args.template_dir,
               args.target, args.owoauth, args.sustain)


if __name__ == "__main__":
    sys.exit(main())
