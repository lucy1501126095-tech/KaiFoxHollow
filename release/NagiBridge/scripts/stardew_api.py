"""NagiBridge API helper — thin wrapper around the HTTP endpoints."""

import requests
import time
import sys

import os
BASE_URL = os.environ.get("NAGI_URL", "http://localhost:7842")


def _get(endpoint, params=None):
    r = requests.get(f"{BASE_URL}{endpoint}", params=params, timeout=10)
    return r.json()


def _post(endpoint, data=None):
    r = requests.post(f"{BASE_URL}{endpoint}", json=data or {}, timeout=10)
    return r.json()


# ── Basic queries ──

def status():
    return _get("/status")


def state():
    return _get("/state")


def surroundings(radius=10):
    return _get("/surroundings", {"radius": radius})


# ── Actions ──

def move_to(x, y, timeout=15):
    """Move to tile (x, y) and block until arrival or timeout."""
    _post("/move", {"x": x, "y": y})
    deadline = time.time() + timeout
    while time.time() < deadline:
        s = state()
        p = s.get("player", {})
        if not p.get("isMoving", False):
            return True
        time.sleep(0.25)
    stop()
    return False


def stop():
    return _post("/stop")


def face(direction):
    """Set facing direction: 0=up 1=right 2=down 3=left"""
    return _post("/face", {"direction": direction})


def select(name):
    """Select an inventory item by name."""
    return _post("/select", {"name": name})


def use_tool(name="current"):
    """Swing a tool by name (or 'current')."""
    return _post("/tool", {"name": name})


def use_item(force=False):
    """Use the currently held item (place seed, use tool, etc.)."""
    return _post("/use", {"force": force} if force else {})


def interact():
    return _post("/interact")


def chat(message):
    return _post("/chat", {"message": message})


def emote(emote_id):
    return _post("/emote", {"id": emote_id})


# ── Convenience helpers ──

def player_tile():
    """Return (x, y) of current player tile."""
    s = state()
    p = s["player"]
    return p["x"], p["y"]


def player_health():
    """Return (current, max) health."""
    s = state()
    p = s["player"]
    return p["health"], p["maxHealth"]


def player_stamina():
    """Return (current, max) stamina."""
    s = state()
    p = s["player"]
    return p["stamina"], p["maxStamina"]


def current_location():
    s = state()
    return s["location"]["name"]


def watering_can_water():
    """Return (waterLeft, waterMax) or (None, None) if not found."""
    s = state()
    for item in s.get("inventory", []):
        if item.get("name") == "Watering Can":
            return item.get("waterLeft"), item.get("waterMax")
    return None, None


def refill_water():
    """Refill watering can via /refill endpoint."""
    return _post("/refill")


def menu():
    return _get("/menu")


def menu_click(option=None, button=None, x=None, y=None):
    data = {}
    if option is not None: data["option"] = option
    if button is not None: data["button"] = button
    if x is not None: data["x"] = x
    if y is not None: data["y"] = y
    return _post("/menu/click", data)


def craft(name, count=1):
    return _post("/craft", {"name": name, "count": count})


def machines():
    return _get("/machines")


def animals():
    return _get("/animals")


def warp(location, x=None, y=None):
    data = {"location": location}
    if x is not None: data["x"] = x
    if y is not None: data["y"] = y
    return _post("/warp", data)


def sell(name=None, sell_all=False):
    data = {}
    if name: data["name"] = name
    if sell_all: data["all"] = True
    return _post("/sell", data)


def key(k, count=1):
    return _post("/key", {"key": k, "count": count})


def wait_tool_animation(seconds=0.6):
    """Wait for tool animation to finish."""
    time.sleep(seconds)


# ── Farm layout constants ──
SHIPPING_BIN = (71, 14)
CHEST_POSITIONS = [(70, 14), (69, 14)]
FURNACE_POSITIONS = [(73, 14), (74, 14)]


def face_toward(tx, ty):
    """Calculate face direction from actual player position to target tile."""
    s = state()
    px, py = s["player"]["x"], s["player"]["y"]
    dx, dy = tx - px, ty - py
    if dx == 0 and dy == 0:
        return 2
    if abs(dx) > abs(dy):
        return 1 if dx > 0 else 3
    return 2 if dy > 0 else 0


def interact_machine(mx, my):
    """Walk to a machine and interact — tries multiple angles until facing matches."""
    approaches = [(mx, my+1), (mx, my-1), (mx-1, my), (mx+1, my)]
    for nx, ny in approaches:
        move_to(nx, ny, timeout=10)
        time.sleep(0.2)
        d = face_toward(mx, my)
        face(d)
        time.sleep(0.15)
        s = state()
        px, py = s["player"]["x"], s["player"]["y"]
        fd = s["player"]["facingDirection"]
        fdx = [0, 1, 0, -1][fd]
        fdy = [-1, 0, 1, 0][fd]
        if px + fdx == mx and py + fdy == my:
            interact()
            time.sleep(0.5)
            return True
    interact()
    time.sleep(0.5)
    return False


def log(msg):
    try:
        print(f"[NagiBridge] {msg}", flush=True)
    except UnicodeEncodeError:
        print(f"[NagiBridge] {msg.encode('utf-8', errors='replace').decode('utf-8')}", flush=True)
