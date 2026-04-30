"""Chop trees — find nearby trees, chop with dynamic facing from actual position."""

import argparse
import os
import time

parser = argparse.ArgumentParser()
parser.add_argument("--count", type=int, default=10, help="Max trees to chop")
parser.add_argument("--port", type=int, default=7842)
args = parser.parse_args()

os.environ["NAGI_URL"] = f"http://localhost:{args.port}"
import stardew_api as api

MAX_HITS = 15
HIT_DELAY = 0.7
STAMINA_RESERVE = 10


def find_trees(radius=20):
    data = api.surroundings(radius)
    px, py = data["center"]["x"], data["center"]["y"]
    trees = []
    for t in data.get("tiles", []):
        terrain = t.get("terrain", "")
        if terrain.startswith("Tree:"):
            d = abs(t["x"] - px) + abs(t["y"] - py)
            trees.append((t["x"], t["y"], terrain, d))
    trees.sort(key=lambda t: t[3])
    return trees


def tree_still_there(tx, ty):
    data = api.surroundings(3)
    for t in data.get("tiles", []):
        if t["x"] == tx and t["y"] == ty and t.get("terrain", "").startswith("Tree:"):
            return True
    return False


def face_toward(tx, ty):
    s = api.state()
    px, py = s["player"]["x"], s["player"]["y"]
    dx, dy = tx - px, ty - py
    if dx == 0 and dy == 0:
        return 2
    if abs(dx) > abs(dy):
        return 1 if dx > 0 else 3
    return 2 if dy > 0 else 0


def try_chop(tx, ty):
    approaches = [
        (tx, ty + 1),
        (tx, ty - 1),
        (tx - 1, ty),
        (tx + 1, ty),
    ]

    api.select("Axe")
    time.sleep(0.15)

    for nx, ny in approaches:
        arrived = api.move_to(nx, ny, timeout=8)
        if not arrived:
            continue

        time.sleep(0.2)
        direction = face_toward(tx, ty)
        api.face(direction)
        time.sleep(0.15)

        # verify we're actually facing the tree
        s = api.state()
        px, py = s["player"]["x"], s["player"]["y"]
        fd = s["player"]["facingDirection"]
        face_dx = [0, 1, 0, -1][fd]
        face_dy = [-1, 0, 1, 0][fd]
        facing_x, facing_y = px + face_dx, py + face_dy
        if facing_x != tx or facing_y != ty:
            api.log(f"  At ({px},{py}) facing ({facing_x},{facing_y}), tree at ({tx},{ty}) — skip this angle")
            continue

        api.log(f"  Chopping from ({px},{py}) facing dir={fd}")
        for hit in range(MAX_HITS):
            cur, mx = api.player_stamina()
            if cur < STAMINA_RESERVE:
                api.log(f"  Stamina low ({cur:.0f})")
                return "stamina"

            api.use_tool("Axe")
            time.sleep(HIT_DELAY)

            if hit > 0 and hit % 5 == 0:
                if not tree_still_there(tx, ty):
                    return "chopped"

        if not tree_still_there(tx, ty):
            return "chopped"

    return "failed"


def run():
    api.log(f"=== Chop Trees (max {args.count}) ===")

    s = api.state()
    inv = s.get("inventory", [])
    wood_before = sum(i["stack"] for i in inv if i and i["name"] == "Wood")
    api.log(f"Wood before: {wood_before}")

    chopped = 0
    for attempt in range(args.count):
        trees = find_trees()
        if not trees:
            api.log("No more trees nearby")
            break

        tx, ty, ttype, dist = trees[0]
        api.log(f"Tree #{chopped+1}: ({tx},{ty}) {ttype} dist={dist}")

        result = try_chop(tx, ty)
        api.log(f"  Result: {result}")

        if result == "chopped":
            chopped += 1
            time.sleep(0.3)
            api.move_to(tx, ty, timeout=5)
            time.sleep(0.5)
        elif result == "stamina":
            break
        elif result == "failed":
            api.log(f"  Couldn't reach tree at ({tx},{ty}), skipping")

    s = api.state()
    inv = s.get("inventory", [])
    wood_after = sum(i["stack"] for i in inv if i and i["name"] == "Wood")
    api.log(f"Chopped {chopped} trees. Wood: {wood_before} -> {wood_after} (+{wood_after - wood_before})")
    api.log(f"Stamina: {s['player']['stamina']:.0f}/{s['player']['maxStamina']}")
    api.log(f"Time: {s['time']['timeOfDay']}")


if __name__ == "__main__":
    run()
