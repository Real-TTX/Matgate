#!/usr/bin/env python3
"""Matgate browser farm - control API.

Runs a fixed POOL of isolated browser sessions, each on its own X display + x11vnc port,
so Matgate can allocate a slot ("acquire"), connect Guacamole VNC to it, and free it again
("release"). No Docker socket involved: this is an ordinary container that owns its own pool
of processes. Guacamole/guacd reaches the VNC ports over the internal Docker network.

HTTP API (all POST/GET bodies are JSON; mutating calls require the shared token):
  GET  /health                     -> 200 "ok"                (no auth; for compose healthcheck)
  GET  /status                     -> pool + slot list        (auth)
  POST /acquire {url, browser}     -> {slot, port, browser..}  (auth)   503 if pool exhausted
  POST /release {slot|port}        -> {released: true}         (auth)

Environment:
  FARM_CONTROL_PORT        HTTP control port                     (default 8090)
  FARM_POOL_SIZE           number of concurrent sessions         (default 10)
  FARM_VNC_BASE_PORT       first VNC/RFB port                     (default 5901)
  FARM_DISPLAY_BASE        first X display number                 (default 1)
  FARM_GEOMETRY            Xvfb screen geometry                   (default 1280x800x24)
  FARM_SESSION_MAX_MINUTES safety reaper max age (0 disables)     (default 180)
  FARM_TOKEN / FARM_TOKEN_FILE  shared control token (Matgate writes the file)
"""

import json
import os
import pwd
import shutil
import signal
import subprocess
import threading
import time
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

CONTROL_PORT = int(os.environ.get("FARM_CONTROL_PORT", "8090"))
POOL_SIZE = int(os.environ.get("FARM_POOL_SIZE", "10"))
VNC_BASE_PORT = int(os.environ.get("FARM_VNC_BASE_PORT", "5901"))
DISPLAY_BASE = int(os.environ.get("FARM_DISPLAY_BASE", "1"))
GEOMETRY = os.environ.get("FARM_GEOMETRY", "1280x800x24")
SESSION_MAX_MINUTES = int(os.environ.get("FARM_SESSION_MAX_MINUTES", "180"))
TOKEN_ENV = os.environ.get("FARM_TOKEN", "")
TOKEN_FILE = os.environ.get("FARM_TOKEN_FILE", "")

BROWSERS = ("chromium", "firefox")


def now_iso():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def read_token():
    if TOKEN_ENV:
        return TOKEN_ENV.strip()
    if TOKEN_FILE:
        try:
            with open(TOKEN_FILE, "r", encoding="utf-8") as handle:
                return handle.read().strip()
        except OSError:
            return ""
    return ""


class Slot:
    def __init__(self, index):
        self.index = index
        self.display = DISPLAY_BASE + index
        self.port = VNC_BASE_PORT + index
        self.busy = False
        self.browser = ""
        self.url = ""
        self.started_at = 0.0
        self.procs = []  # list[subprocess.Popen]

    def info(self):
        return {
            "slot": self.index,
            "port": self.port,
            "display": self.display,
            "busy": self.busy,
            "browser": self.browser,
            "url": self.url,
            "startedAt": (
                datetime.fromtimestamp(self.started_at, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
                if self.started_at else None
            ),
            "ageSeconds": int(time.time() - self.started_at) if self.busy and self.started_at else 0,
        }


class Farm:
    def __init__(self):
        self.lock = threading.RLock()
        self.slots = [Slot(i) for i in range(POOL_SIZE)]

    def status(self):
        with self.lock:
            return {
                "poolSize": POOL_SIZE,
                "vncBasePort": VNC_BASE_PORT,
                "geometry": GEOMETRY,
                "busy": sum(1 for s in self.slots if s.busy),
                "free": sum(1 for s in self.slots if not s.busy),
                "slots": [s.info() for s in self.slots],
            }

    def acquire(self, url, browser):
        browser = (browser or "chromium").lower()
        if browser not in BROWSERS:
            browser = "chromium"
        with self.lock:
            slot = next((s for s in self.slots if not s.busy), None)
            if slot is None:
                return None
            # Reserve immediately so a concurrent acquire cannot grab the same slot.
            slot.busy = True
            slot.browser = browser
            slot.url = url
            slot.started_at = time.time()
        try:
            self._start_slot(slot, url, browser)
        except Exception:
            # Roll back the reservation and tear down anything half-started.
            self._teardown_slot(slot)
            with self.lock:
                slot.busy = False
                slot.browser = ""
                slot.url = ""
                slot.started_at = 0.0
            raise
        return slot.info()

    def release(self, index=None, port=None):
        with self.lock:
            slot = None
            if index is not None and 0 <= index < POOL_SIZE:
                slot = self.slots[index]
            elif port is not None:
                slot = next((s for s in self.slots if s.port == port), None)
            if slot is None or not slot.busy:
                return False
        self._teardown_slot(slot)
        with self.lock:
            slot.busy = False
            slot.browser = ""
            slot.url = ""
            slot.started_at = 0.0
            slot.procs = []
        return True

    def reap(self):
        if SESSION_MAX_MINUTES <= 0:
            return
        cutoff = time.time() - SESSION_MAX_MINUTES * 60
        stale = []
        with self.lock:
            for slot in self.slots:
                if slot.busy and slot.started_at and slot.started_at < cutoff:
                    stale.append(slot.index)
        for index in stale:
            self.release(index=index)

    @staticmethod
    def _slot_user(index):
        return f"farmslot{index}"

    def _ensure_user(self, user):
        try:
            return pwd.getpwnam(user)
        except KeyError:
            pass
        subprocess.run(
            ["useradd", "-M", "-r", "-s", "/usr/sbin/nologin", user],
            check=False, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )
        return pwd.getpwnam(user)

    # --- process management -------------------------------------------------
    def _start_slot(self, slot, url, browser):
        display = f":{slot.display}"
        env = dict(os.environ, DISPLAY=display)
        profile = f"/tmp/farm-profile-{slot.index}"
        shutil.rmtree(profile, ignore_errors=True)
        os.makedirs(profile, exist_ok=True)

        # Each slot's BROWSER runs as its own throwaway Linux user, so release can nuke every last
        # (possibly detached) browser process with `pkill -u`. The X/VNC helpers stay root + tracked.
        user = self._slot_user(slot.index)
        pw = self._ensure_user(user)
        os.chown(profile, pw.pw_uid, pw.pw_gid)

        def spawn(args):
            proc = subprocess.Popen(
                args, env=env, start_new_session=True,
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
            )
            slot.procs.append(proc)
            return proc

        # 1) Virtual X server (-ac: allow the unprivileged slot user to connect to this display).
        spawn(["Xvfb", display, "-screen", "0", GEOMETRY, "-nolisten", "tcp", "-ac"])
        self._wait_for_x(slot.display, timeout=10.0)

        # 2) Minimal window manager so the browser fills the screen.
        spawn(["fluxbox"])
        time.sleep(0.4)

        # 3) VNC server exposing this display (internal network only -> no VNC password). Run in the
        # FOREGROUND (no -bg): -bg would fork a daemon we cannot track/kill on release.
        spawn([
            "x11vnc", "-display", display, "-rfbport", str(slot.port),
            "-forever", "-shared", "-nopw", "-noxdamage", "-quiet",
        ])
        self._wait_for_port(slot.port, timeout=10.0)

        # 4) The browser, opening the requested URL, isolated per slot + run as the slot user.
        width, height = GEOMETRY.split("x")[0], GEOMETRY.split("x")[1]
        run_as = [
            "runuser", "-u", user, "--",
            "env", f"DISPLAY={display}", f"HOME={profile}",
            f"XDG_CONFIG_HOME={profile}", f"XDG_CACHE_HOME={profile}",
        ]
        if browser == "firefox":
            spawn(run_as + [
                "firefox-esr", "--no-remote", "--new-instance",
                "--profile", profile, "--width", width, "--height", height, url,
            ])
        else:
            spawn(run_as + [
                "chromium", "--no-sandbox", "--disable-gpu", "--no-first-run",
                "--disable-infobars", "--disable-features=Translate,TranslateUI",
                "--disable-dev-shm-usage",
                "--start-maximized", f"--window-size={width},{height}",
                "--window-position=0,0", f"--user-data-dir={profile}", url,
            ])

    def _teardown_slot(self, slot):
        # Build the target PID set while the tree is still intact: (a) every tracked process AND all
        # of its descendants - this catches sandboxed browser renderers, which setsid (escaping the
        # process group) AND scrub their environment (escaping the DISPLAY match); plus (b) anything
        # still tagged with this slot's X display, as a belt-and-suspenders for re-parented helpers.
        ppid_map = self._proc_ppid_map()
        own_pid = os.getpid()
        try:
            uid = pwd.getpwnam(self._slot_user(slot.index)).pw_uid
        except KeyError:
            uid = None

        targets = set()
        for proc in slot.procs:
            targets.add(proc.pid)
            targets.update(self._descendants(proc.pid, ppid_map))
        targets.update(self._pids_by_display(slot.display))
        if uid is not None:
            # The reliable catch-all: every LIVE process owned by the slot user (browser + all its
            # helpers/renderers), matched on the REAL uid via /proc/*/status (pkill -u is unreliable).
            targets.update(self._pids_by_uid(uid))
        targets.discard(own_pid)

        for pid in targets:
            self._safe_kill(pid, signal.SIGTERM)
        time.sleep(1.2)
        survivors = set(targets) | self._pids_by_display(slot.display)
        if uid is not None:
            survivors |= self._pids_by_uid(uid)
        survivors.discard(own_pid)
        for pid in survivors:
            self._safe_kill(pid, signal.SIGKILL)
        # Reap OUR OWN direct children (Xvfb/x11vnc/fluxbox/runuser) so they don't linger as zombies.
        # Browser grandchildren that re-parent to PID 1 are reaped by the container init (run with
        # `init: true`, which the provided compose sets).
        for proc in slot.procs:
            try:
                proc.wait(timeout=2)
            except Exception:
                pass
        shutil.rmtree(f"/tmp/farm-profile-{slot.index}", ignore_errors=True)

    @staticmethod
    def _pids_by_uid(uid):
        own_pid = os.getpid()
        pids = set()
        for entry in os.listdir("/proc"):
            if not entry.isdigit() or int(entry) == own_pid:
                continue
            try:
                with open(f"/proc/{entry}/status", "r", encoding="latin1") as handle:
                    for line in handle:
                        if line.startswith("Uid:"):
                            if int(line.split()[1]) == uid:
                                pids.add(int(entry))
                            break
            except (OSError, ValueError):
                continue
        return pids

    @staticmethod
    def _safe_kill(pid, sig):
        try:
            os.kill(pid, sig)
        except (ProcessLookupError, PermissionError, OSError):
            pass

    @staticmethod
    def _proc_ppid_map():
        mapping = {}
        for entry in os.listdir("/proc"):
            if not entry.isdigit():
                continue
            try:
                with open(f"/proc/{entry}/stat", "rb") as handle:
                    data = handle.read().decode("latin1")
                # "pid (comm) state ppid ..." - comm may contain spaces/parens, so split after ')'.
                fields = data[data.rfind(")") + 2:].split()
                ppid = int(fields[1])
            except (OSError, ValueError, IndexError):
                continue
            mapping.setdefault(ppid, []).append(int(entry))
        return mapping

    @staticmethod
    def _descendants(root_pid, ppid_map):
        found = []
        stack = [root_pid]
        while stack:
            current = stack.pop()
            for child in ppid_map.get(current, []):
                found.append(child)
                stack.append(child)
        return found

    @staticmethod
    def _pids_by_display(display):
        # environ entries are NUL-separated, so the trailing \x00 makes ":1" not match ":10".
        needle = f"DISPLAY=:{display}\x00".encode()
        own_pid = os.getpid()
        pids = set()
        for entry in os.listdir("/proc"):
            if not entry.isdigit() or int(entry) == own_pid:
                continue
            try:
                with open(f"/proc/{entry}/environ", "rb") as handle:
                    if needle in handle.read():
                        pids.add(int(entry))
            except OSError:
                continue
        return pids

    @staticmethod
    def _wait_for_x(display, timeout):
        sock = f"/tmp/.X11-unix/X{display}"
        deadline = time.time() + timeout
        while time.time() < deadline:
            if os.path.exists(sock):
                return
            time.sleep(0.1)
        raise RuntimeError(f"Xvfb :{display} did not start")

    @staticmethod
    def _wait_for_port(port, timeout):
        import socket
        deadline = time.time() + timeout
        while time.time() < deadline:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
                probe.settimeout(0.3)
                if probe.connect_ex(("127.0.0.1", port)) == 0:
                    return
            time.sleep(0.15)
        raise RuntimeError(f"x11vnc did not listen on {port}")


FARM = Farm()


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *args):
        pass

    def _send(self, code, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _authorized(self):
        token = read_token()
        if not token:
            # No token configured yet -> deny mutating calls (fail closed).
            return False
        provided = self.headers.get("X-Farm-Token", "")
        # Constant-time compare.
        if len(provided) != len(token):
            return False
        result = 0
        for a, b in zip(provided, token):
            result |= ord(a) ^ ord(b)
        return result == 0

    def _read_json(self):
        length = int(self.headers.get("Content-Length", "0") or "0")
        if length <= 0:
            return {}
        try:
            return json.loads(self.rfile.read(length).decode("utf-8") or "{}")
        except (ValueError, UnicodeDecodeError):
            return {}

    def do_GET(self):
        if self.path == "/health":
            self._send(200, {"status": "ok"})
            return
        if self.path == "/status":
            if not self._authorized():
                self._send(401, {"error": "unauthorized"})
                return
            self._send(200, FARM.status())
            return
        self._send(404, {"error": "not_found"})

    def do_POST(self):
        if not self._authorized():
            self._send(401, {"error": "unauthorized"})
            return
        data = self._read_json()
        if self.path == "/acquire":
            url = (data.get("url") or "").strip()
            if not (url.startswith("http://") or url.startswith("https://")):
                self._send(400, {"error": "invalid_url"})
                return
            try:
                info = FARM.acquire(url, data.get("browser"))
            except Exception as exc:  # noqa: BLE001 - report start failures to Matgate
                self._send(500, {"error": "start_failed", "detail": str(exc)})
                return
            if info is None:
                self._send(503, {"error": "pool_exhausted"})
                return
            self._send(200, info)
            return
        if self.path == "/release":
            released = FARM.release(
                index=data.get("slot") if isinstance(data.get("slot"), int) else None,
                port=data.get("port") if isinstance(data.get("port"), int) else None,
            )
            self._send(200, {"released": released})
            return
        self._send(404, {"error": "not_found"})


def reaper_loop():
    while True:
        time.sleep(30)
        try:
            FARM.reap()
        except Exception:
            pass


def main():
    threading.Thread(target=reaper_loop, daemon=True).start()
    server = ThreadingHTTPServer(("0.0.0.0", CONTROL_PORT), Handler)
    print(f"browser-farm control API on :{CONTROL_PORT}, pool={POOL_SIZE}, vnc={VNC_BASE_PORT}..{VNC_BASE_PORT + POOL_SIZE - 1}", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
