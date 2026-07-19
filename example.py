"""Runnable demo for the pure-Python OWO SDK.

Two modes:

    py -3.11 example.py --dry      Print the exact wire strings, no hardware.
    py -3.11 example.py            Discover, connect to a running OWO app and
                                   fire a few sensations on the suit.

The OWO app must be running and the suit powered on for the live mode. Auth uses
game id "0" (anonymous); replace it with your registered OWO game id once you
have one so custom sensations and icons resolve in the OWO app.
"""

import logging
import sys
import time

import owo
from owo import Muscle, OWOClient, sensation


def dry_run():
    demos = {
        "single pulse (chest R)":
            sensation(frequency=80, duration_s=0.5, intensity=60).with_muscles(Muscle.PECTORAL_R),
        "ramped heartbeat (both chest)":
            sensation(frequency=40, duration_s=0.3, intensity=80, ramp_up_s=0.1, ramp_down_s=0.1)
            .with_muscles(Muscle.PECTORAL_R, Muscle.PECTORAL_L),
        "dagger sequence":
            owo.dagger().with_muscles(Muscle.ABDOMINAL_R),
        "shot through torso":
            owo.shot_with_exit(),
        "full front at 50 percent":
            sensation(60, 1.0, 100).with_muscles(*owo.muscles_front()).multiply_intensity(50),
    }
    for label, s in demos.items():
        print(f"{label:32s} -> 0*SENSATION*{owo.serialize(s)}")


def live_run():
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    client = OWOClient(game_id="0")
    client.open()
    print("Searching for OWO app on the network...")
    if not client.auto_connect(timeout=15):
        print("No OWO app found. Is the desktop app running and the suit on?")
        client.close()
        return
    print(f"Connected to {client.connected_servers}")

    client.send(sensation(80, 0.5, 60), Muscle.PECTORAL_R, Muscle.PECTORAL_L)
    time.sleep(1.0)
    client.send(owo.dagger(), Muscle.ABDOMINAL_R)
    time.sleep(2.5)
    client.send(owo.shot_with_exit())
    time.sleep(2.0)
    client.stop()
    client.close()
    print("Done.")


if __name__ == "__main__":
    if "--dry" in sys.argv:
        dry_run()
    else:
        live_run()
