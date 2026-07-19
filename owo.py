"""Pure-Python OWO haptics SDK.

A dependency-free reimplementation of the OWO game SDK wire protocol, rebuilt by
decompiling the official OWO.dll (NuGet package `OWO` 2.4.2, MIT licensed) and
reproducing its behavior byte-for-byte over UDP. It replaces the pythonnet +
`OWO.dll` + .NET runtime stack the app previously needed to talk to an OWO suit
with a single standard-library module.

The full reverse-engineered wire format lives in OWO_PROTOCOL.md next to this
file. In short: every message is ASCII over UDP to port 54020 on a single
unbound socket. Discovery is `ping` -> app replies `okay` -> we reply
`{gameId}*AUTH*{gameAuth}` -> app replies `pong` -> connected. Sensations are
`{gameId}*SENSATION*{sensation}`, stop is `{gameId}*STOP`, and the app announces
its own shutdown with `OWO_Close`.

A sensation is a comma-separated micro-sensation
`frequency,duration_ds,intensity,rampUp_ms,rampDown_ms,exitDelay_ds,name`
optionally suffixed with `|id%intensity,...` muscle targets, and sequences are
joined with `&`. Duration and exit-delay are serialized in deciseconds; ramp-up
and ramp-down in milliseconds.

Typical use:

    from owo import OWOClient, sensation, Muscle

    owo = OWOClient(game_id="0")
    owo.open()
    owo.auto_connect(timeout=10)
    owo.send(sensation(frequency=80, duration_s=0.5, intensity=60),
             Muscle.PECTORAL_R, Muscle.PECTORAL_L)
    owo.stop()
    owo.close()
"""

import logging
import socket
import threading
import time

DEFAULT_PORT = 54020
BROADCAST = "255.255.255.255"
RECV_BUFFER = 1024

DISCONNECTED = "Disconnected"
CONNECTING = "Connecting"
CONNECTED = "Connected"

_log = logging.getLogger("owo")

__all__ = [
    "OWOClient", "SustainedPlayback",
    "Muscle", "muscles_front", "muscles_back", "muscles_all", "mirror_muscles",
    "Icon",
    "Sensation", "MicroSensation", "SensationWithMuscles", "SensationsSequence",
    "BakedSensation",
    "sensation", "serialize", "parse", "parse_muscles",
    "build_game_auth", "load_owoauth", "save_owoauth",
    "ball", "dart", "dagger_entry", "dagger_movement", "dagger",
    "shot_entry", "shot_exit", "shot_bleeding", "shot_with_exit",
    "DEFAULT_PORT", "BROADCAST", "DISCONNECTED", "CONNECTING", "CONNECTED",
]


def _clamp(value, low, high):
    if value < low:
        return low
    if value > high:
        return high
    return value


def _round1(value):
    return round(float(value), 1)


class Muscle:
    """A single OWO suit muscle (id 0-9) with a 0-100 intensity multiplier."""

    __slots__ = ("id", "intensity")

    def __init__(self, muscle_id, intensity=100):
        self.id = int(muscle_id)
        self.intensity = int(_clamp(int(intensity), 0, 100))

    def with_intensity(self, intensity):
        return Muscle(self.id, intensity)

    def mirror(self):
        mirrored = self.id + 1 if self.id % 2 == 0 else self.id - 1
        return Muscle(mirrored, self.intensity)

    def multiply_intensity(self, percentage):
        return Muscle(self.id, int(self.intensity * int(percentage) / 100))

    def stringify(self):
        return f"{self.id}%{self.intensity}"

    def __repr__(self):
        return f"Muscle(id={self.id}, intensity={self.intensity})"


Muscle.PECTORAL_R = Muscle(0)
Muscle.PECTORAL_L = Muscle(1)
Muscle.ABDOMINAL_R = Muscle(2)
Muscle.ABDOMINAL_L = Muscle(3)
Muscle.ARM_R = Muscle(4)
Muscle.ARM_L = Muscle(5)
Muscle.DORSAL_R = Muscle(6)
Muscle.DORSAL_L = Muscle(7)
Muscle.LUMBAR_R = Muscle(8)
Muscle.LUMBAR_L = Muscle(9)


def muscles_front():
    return [Muscle.PECTORAL_R, Muscle.PECTORAL_L, Muscle.ABDOMINAL_R,
            Muscle.ABDOMINAL_L, Muscle.ARM_R, Muscle.ARM_L]


def muscles_back():
    return [Muscle.DORSAL_R, Muscle.DORSAL_L, Muscle.LUMBAR_R, Muscle.LUMBAR_L]


def muscles_all():
    return muscles_front() + muscles_back()


def mirror_muscles(muscles):
    return [m.mirror() for m in muscles]


class Icon:
    """Named suit-app icon shown for a registered (baked) sensation."""

    EMPTY = "0"
    DEATH = "Death-0"
    SPIDERS = "Spider-0"
    WEIGHT = "Weight-0"
    ENVIRONMENT = "Environment-0"
    ALERT = "Alert-0"
    VICTORY = "Victory-0"
    IMPACT_BALL = "Impact-0"
    IMPACT_DART = "Impact-1"
    IMPACT_PUNCH = "Impact-2"
    IMPACT_BULLET = "Impact-3"
    WEAPON_AXE = "Weapon-0"
    WEAPON_DAGGER = "Weapon-1"
    WEAPON_GUN = "Weapon-2"
    WEAPON_SUBMACHINEGUN = "Weapon-3"


class Sensation:
    """Base class for every playable OWO sensation; serializes to wire text."""

    def __init__(self):
        self.priority = 0

    @property
    def duration(self):
        return 0.0

    def with_priority(self, priority):
        self.priority = int(priority)
        return self

    def with_muscles(self, *muscles):
        flat = _flatten_muscles(muscles)
        if not flat or isinstance(self, SensationWithMuscles):
            return self
        if isinstance(self, SensationsSequence):
            wrapped = [s.with_muscles(*flat) for s in self.sensations]
            return SensationsSequence(*wrapped).with_priority(self.priority)
        return SensationWithMuscles(self, flat).with_priority(self.priority)

    def append(self, other):
        return SensationsSequence(self, other).with_priority(self.priority)

    def multiply_intensity(self, percentage):
        raise NotImplementedError

    def stringify(self):
        return serialize(self)

    def __str__(self):
        return serialize(self)


class MicroSensation(Sensation):
    """A single haptic pulse: frequency, duration, intensity and ramps."""

    def __init__(self, frequency=100, duration_s=0.1, intensity=100,
                 ramp_up_s=0.0, ramp_down_s=0.0, exit_delay_s=0.0, name=""):
        super().__init__()
        self.frequency = int(_clamp(int(frequency), 1, 100))
        self.duration_s = _round1(_clamp(float(duration_s), 0.1, 20.0))
        self.intensity = int(_clamp(int(intensity), 0, 100))
        self.ramp_up_s = _round1(_clamp(float(ramp_up_s), 0.0, 2.0))
        self.ramp_down_s = _round1(_clamp(float(ramp_down_s), 0.0, 2.0))
        self.exit_delay_s = _round1(_clamp(float(exit_delay_s), 0.0, 20.0))
        self.name = name or ""

    @property
    def duration(self):
        return self.duration_s + self.exit_delay_s

    def with_name(self, name):
        return MicroSensation(self.frequency, self.duration_s, self.intensity,
                              self.ramp_up_s, self.ramp_down_s,
                              self.exit_delay_s, name).with_priority(self.priority)

    def multiply_intensity(self, percentage):
        scaled = int(self.intensity * int(percentage) / 100)
        return MicroSensation(self.frequency, self.duration_s, scaled,
                              self.ramp_up_s, self.ramp_down_s,
                              self.exit_delay_s, self.name).with_priority(self.priority)


class SensationWithMuscles(Sensation):
    """A sensation bound to one or more target muscles."""

    def __init__(self, reference, muscles):
        super().__init__()
        self.reference = reference
        self.muscles = list(muscles)

    @property
    def duration(self):
        return self.reference.duration

    def multiply_intensity(self, percentage):
        scaled = [m.multiply_intensity(percentage) for m in self.muscles]
        return SensationWithMuscles(self.reference, scaled).with_priority(self.priority)


class SensationsSequence(Sensation):
    """An ordered chain of sensations played back to back."""

    def __init__(self, *sensations):
        super().__init__()
        self.sensations = list(sensations)

    @property
    def duration(self):
        return sum(s.duration for s in self.sensations)

    def multiply_intensity(self, percentage):
        scaled = [s.multiply_intensity(percentage) for s in self.sensations]
        return SensationsSequence(*scaled).with_priority(self.priority)


class BakedSensation(Sensation):
    """A sensation registered with the OWO app under a numeric id and name."""

    def __init__(self, sensation_id, name, reference, icon=Icon.EMPTY, family=""):
        super().__init__()
        self.id = int(sensation_id)
        self.name = name
        self.reference = reference
        self.icon = icon
        self.family = family

    @property
    def duration(self):
        return self.reference.duration if self.reference is not None else 0.0

    def with_icon(self, icon):
        return BakedSensation(self.id, self.name, self.reference, icon,
                              self.family).with_priority(self.priority)

    def belongs_to(self, family):
        return BakedSensation(self.id, self.name, self.reference, self.icon,
                              family).with_priority(self.priority)

    def multiply_intensity(self, percentage):
        return BakedSensation(self.id, self.name, self.reference, self.icon,
                              self.family).with_priority(self.priority)

    def register_string(self):
        return f"{self.id}~{self.name}~{serialize(self.reference)}~{self.icon}~{self.family}"


def _flatten_muscles(muscles):
    flat = []
    for m in muscles:
        if isinstance(m, Muscle):
            flat.append(m)
        elif isinstance(m, (list, tuple)):
            flat.extend(x for x in m if isinstance(x, Muscle))
    return flat


def serialize(sensation):
    if isinstance(sensation, MicroSensation):
        return (
            f"{sensation.frequency},"
            f"{int(round(sensation.duration_s * 10))},"
            f"{sensation.intensity},"
            f"{int(round(sensation.ramp_up_s * 1000))},"
            f"{int(round(sensation.ramp_down_s * 1000))},"
            f"{int(round(sensation.exit_delay_s * 10))},"
            f"{sensation.name}"
        )
    if isinstance(sensation, SensationWithMuscles):
        muscle_str = ",".join(m.stringify() for m in sensation.muscles)
        return f"{serialize(sensation.reference)}|{muscle_str}"
    if isinstance(sensation, SensationsSequence):
        return "&".join(serialize(s) for s in sensation.sensations)
    if isinstance(sensation, BakedSensation):
        return str(sensation.id)
    raise TypeError(f"Unknown sensation type: {type(sensation)!r}")


def sensation(frequency=100, duration_s=0.1, intensity=100,
              ramp_up_s=0.0, ramp_down_s=0.0, exit_delay_s=0.0, name=""):
    return MicroSensation(frequency, duration_s, intensity,
                          ramp_up_s, ramp_down_s, exit_delay_s, name)


def parse(message):
    if "~" in message:
        parts = message.split("~")
        family = parts[4] if len(parts) >= 5 else ""
        return BakedSensation(int(parts[0]), parts[1], parse(parts[2]),
                              parts[3] if len(parts) >= 4 else Icon.EMPTY, family)
    if "&" in message:
        return SensationsSequence(*[parse(p) for p in message.split("&")])
    if "|" in message:
        left, right = message.split("|", 1)
        return SensationWithMuscles(parse(left), parse_muscles(right))
    parts = message.split(",")
    if len(parts) >= 6:
        name = parts[6] if len(parts) >= 7 else ""
        return MicroSensation(int(parts[0]), float(parts[1]) / 10.0, int(parts[2]),
                              float(parts[3]) / 1000.0, float(parts[4]) / 1000.0,
                              float(parts[5]) / 10.0, name)
    return BakedSensation(int(parts[0]), "", None)


def parse_muscles(message):
    out = []
    for token in message.split(","):
        muscle_id, intensity = token.split("%")
        out.append(Muscle(int(muscle_id), int(intensity)))
    return out


def ball():
    return sensation(100, 0.1)


def dart():
    return sensation(10, 0.1)


def dagger_entry():
    return sensation(60, 0.2)


def dagger_movement():
    return sensation(100, 2.0, 100, 0.3, 0.1)


def dagger():
    return dagger_entry().append(dagger_movement())


def shot_entry():
    return sensation(30, 0.1).with_muscles(Muscle.PECTORAL_R)


def shot_exit():
    return sensation(20, 0.1).with_muscles(Muscle.DORSAL_R)


def shot_bleeding():
    return sensation(50, 0.5, 80, 0.0, 0.3).with_muscles(Muscle.PECTORAL_R, Muscle.PECTORAL_L)


def shot_with_exit():
    return shot_entry().append(shot_exit()).append(shot_bleeding())


def build_game_auth(sensations):
    if not sensations:
        return ""
    return "#\n".join(s.register_string() for s in sensations)


def load_owoauth(path):
    """Load registered baked sensations from a .owoauth export file.

    The file holds one or more baked registration strings
    (id~name~reference~icon~family) joined by '#'. Returns a list of
    BakedSensation ready for OWOClient(registered_sensations=...), which lets the
    OWO app resolve those sensations by id and show their names and icons.
    """
    with open(path, "r", encoding="utf-8") as fh:
        raw = fh.read()
    out = []
    for segment in raw.replace("\r", "").split("#"):
        segment = segment.strip()
        if not segment:
            continue
        try:
            parsed = parse(segment)
        except Exception as exc:
            _log.warning("skipping unparseable .owoauth segment %r: %s", segment, exc)
            continue
        if isinstance(parsed, BakedSensation):
            out.append(parsed)
        else:
            _log.warning("skipping non-baked .owoauth segment %r", segment)
    return out


def save_owoauth(path, sensations):
    """Write baked sensations to a .owoauth file (the inverse of load_owoauth).

    `sensations` is a list of BakedSensation. The file it produces is exactly the
    registration payload sent in the AUTH handshake, so it round-trips with
    load_owoauth and drops straight into OWOClient(registered_sensations=...).
    """
    with open(path, "w", encoding="utf-8") as fh:
        fh.write(build_game_auth(list(sensations)))


class OWOClient:
    """Discovers, connects to and drives OWO suit apps over UDP with no .NET.

    One instance owns a single unbound UDP socket, a background receive thread
    that runs the discovery/keepalive state machine, and the set of connected
    server addresses. Call open() once, then auto_connect() or connect(*ips),
    then send()/stop() as needed and close() on shutdown.
    """

    def __init__(self, game_id="0", registered_sensations=None,
                 port=DEFAULT_PORT, scan_interval=0.5, auto_reconnect=True,
                 keepalive=True, keepalive_timeout=3.0):
        self.game_id = str(game_id)
        self.auth_payload = build_game_auth(registered_sensations or [])
        self.port = int(port)
        self.scan_interval = float(scan_interval)
        self.auto_reconnect = bool(auto_reconnect)
        self.keepalive = bool(keepalive)
        self.keepalive_timeout = float(keepalive_timeout)

        self.state = DISCONNECTED
        self._sock = None
        self._connected = set()
        self._discovered = set()
        self._targets = [BROADCAST]
        self._last_seen = {}

        self._lock = threading.Lock()
        self._running = False
        self._recv_thread = None
        self._scan_thread = None
        self._last_ping = 0.0
        self._sustains = set()

        self._last_priority = -1
        self._busy_until_ms = 0

    @property
    def is_connected(self):
        return self.state == CONNECTED and bool(self._connected)

    @property
    def discovered_apps(self):
        with self._lock:
            return sorted(self._discovered)

    @property
    def connected_servers(self):
        with self._lock:
            return sorted(self._connected)

    def open(self):
        if self._sock is not None:
            return
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        s.settimeout(0.5)
        self._sock = s
        self._running = True
        self._recv_thread = threading.Thread(target=self._recv_loop, daemon=True,
                                              name="OWO-Recv")
        self._recv_thread.start()
        _log.info("OWO socket open on ephemeral port; target port %d", self.port)

    def close(self):
        self._stop_sustains()
        self._running = False
        with self._lock:
            self.state = DISCONNECTED
            self._connected.clear()
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
            self._sock = None

    def configure(self, game_id=None, registered_sensations=None):
        if game_id is not None:
            self.game_id = str(game_id)
        if registered_sensations is not None:
            self.auth_payload = build_game_auth(registered_sensations)

    def auto_connect(self, timeout=None, block=True):
        return self._begin_connect([BROADCAST], timeout, block)

    def connect(self, *ips, timeout=None, block=True):
        targets = list(ips) or [BROADCAST]
        return self._begin_connect(targets, timeout, block)

    def _begin_connect(self, targets, timeout, block):
        if self._sock is None:
            self.open()
        with self._lock:
            self._targets = list(targets)
            self.state = CONNECTING
        self._start_scan()
        if not block:
            return False
        deadline = None if timeout is None else time.time() + timeout
        while self._running:
            if self.is_connected:
                return True
            if deadline is not None and time.time() >= deadline:
                return False
            time.sleep(0.05)
        return False

    def disconnect(self):
        with self._lock:
            self.state = DISCONNECTED
            self._connected.clear()

    def send(self, a_sensation, *muscles, priority=None, force=False):
        if not self.is_connected:
            return False
        payload = a_sensation.with_muscles(*muscles) if muscles else a_sensation
        if priority is not None:
            payload = payload.with_priority(priority)
        now_ms = int(time.monotonic() * 1000)
        if not force and not self._priority_allows(payload, now_ms):
            return False
        self._broadcast_to_connected(f"{self.game_id}*SENSATION*{serialize(payload)}")
        self._busy_until_ms = now_ms + int(payload.duration * 1000)
        self._last_priority = payload.priority
        return True

    def stop(self):
        self._stop_sustains()
        self._broadcast_to_connected(f"{self.game_id}*STOP")
        self._last_priority = -1
        self._busy_until_ms = 0

    def sustain(self, a_sensation, *muscles, priority=None, lead_s=0.05):
        """Keep a sensation playing continuously by re-sending it on a loop.

        The OWO protocol ends the current sensation on every new send, so a held
        effect must be re-sent just before it elapses. Returns a SustainedPlayback
        handle: call handle.set_intensity(pct) to scale it live (e.g. from a touch
        level), handle.update(sensation) to swap it, and handle.stop() to end it.
        """
        base = a_sensation.with_muscles(*muscles) if muscles else a_sensation
        handle = SustainedPlayback(self, base, priority, lead_s)
        with self._lock:
            self._sustains.add(handle)
        return handle

    def _stop_sustains(self):
        with self._lock:
            handles = list(self._sustains)
            self._sustains.clear()
        for handle in handles:
            handle.stop(_send_stop=False)

    def _priority_allows(self, payload, now_ms):
        return self._last_priority <= payload.priority or now_ms >= self._busy_until_ms

    def _broadcast_to_connected(self, message):
        for ip in self.connected_servers:
            self._send_raw(message, ip)

    def _send_raw(self, message, ip):
        if self._sock is None:
            return
        try:
            self._sock.sendto(message.encode("ascii"), (ip, self.port))
        except OSError as exc:
            _log.debug("OWO send to %s failed: %s", ip, exc)

    def _start_scan(self):
        if self._scan_thread and self._scan_thread.is_alive():
            return
        self._scan_thread = threading.Thread(target=self._scan_loop, daemon=True,
                                              name="OWO-Scan")
        self._scan_thread.start()

    def _scan_loop(self):
        while self._running:
            if self.state == CONNECTING or (self.auto_reconnect and not self.is_connected):
                for target in list(self._targets):
                    self._send_raw("ping", target)
            if self.keepalive and self._connected:
                self._keepalive_tick()
            time.sleep(self.scan_interval)

    def _keepalive_tick(self):
        now = time.monotonic()
        for ip in self.connected_servers:
            self._send_raw("ping", ip)
        with self._lock:
            stale = [ip for ip in list(self._connected)
                     if now - self._last_seen.get(ip, now) > self.keepalive_timeout]
            for ip in stale:
                self._connected.discard(ip)
                self._last_seen.pop(ip, None)
            if stale and not self._connected:
                self.state = CONNECTING if self.auto_reconnect else DISCONNECTED
        for ip in stale:
            _log.warning("OWO server %s stopped replying; dropped (will re-discover)", ip)

    def _recv_loop(self):
        while self._running and self._sock is not None:
            try:
                data, addr = self._sock.recvfrom(RECV_BUFFER)
            except socket.timeout:
                continue
            except OSError:
                break
            message = data.decode("ascii", errors="ignore").strip()
            self._handle_message(message, addr[0])

    def _handle_message(self, message, sender_ip):
        if not message:
            return
        with self._lock:
            self._last_seen[sender_ip] = time.monotonic()
        if message == "okay":
            with self._lock:
                self._discovered.add(sender_ip)
                already = sender_ip in self._connected
            if not already:
                self._send_raw(f"{self.game_id}*AUTH*{self.auth_payload}", sender_ip)
                _log.debug("OWO app available at %s; AUTH sent", sender_ip)
        elif message == "pong":
            with self._lock:
                self._connected.add(sender_ip)
                self.state = CONNECTED
            _log.info("OWO connected to %s", sender_ip)
        elif message == "OWO_Close":
            with self._lock:
                self._connected.discard(sender_ip)
                self._last_seen.pop(sender_ip, None)
                if not self._connected:
                    self.state = CONNECTING if self.auto_reconnect else DISCONNECTED
            _log.info("OWO app %s closed", sender_ip)
            if self.auto_reconnect:
                self._start_scan()


class SustainedPlayback:
    """A background loop that re-sends a sensation so it plays continuously.

    Created by OWOClient.sustain(); do not construct directly. The OWO app ends the
    current sensation on every new send, so this re-sends the sensation just before
    it elapses, giving a held/continuous effect. Adjust it live with
    set_intensity()/update() and end it with stop(). Safe across reconnects: while
    disconnected the underlying send is a no-op and playback resumes on reconnect.
    """

    def __init__(self, client, base_sensation, priority, lead_s):
        self._client = client
        self._base = base_sensation
        self._current = base_sensation
        self._priority = priority
        self._lead_s = max(0.0, float(lead_s))
        self._lock = threading.Lock()
        self._stop_evt = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True, name="OWO-Sustain")
        self._thread.start()

    @property
    def running(self):
        return not self._stop_evt.is_set()

    def set_intensity(self, percentage):
        """Scale the ORIGINAL sensation to `percentage` of its authored intensity.

        Always derived from the base sensation, so repeated calls do not compound.
        Ideal for tracking a live 0-100 touch/level value.
        """
        with self._lock:
            self._current = self._base.multiply_intensity(percentage)

    def update(self, a_sensation):
        """Replace the sustained sensation outright while continuing to loop."""
        with self._lock:
            self._base = a_sensation
            self._current = a_sensation

    def stop(self, _send_stop=True):
        if self._stop_evt.is_set():
            return
        self._stop_evt.set()
        with self._client._lock:
            self._client._sustains.discard(self)
        if _send_stop:
            self._client._broadcast_to_connected(f"{self._client.game_id}*STOP")

    def _run(self):
        while not self._stop_evt.is_set():
            with self._lock:
                current = self._current
            self._client.send(current, priority=self._priority, force=True)
            interval = max(0.05, current.duration - self._lead_s)
            self._stop_evt.wait(interval)
