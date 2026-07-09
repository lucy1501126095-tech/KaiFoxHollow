"""
kai_brain v2 端到端测试(不需要真游戏、不花token)。

内嵌一个mock NagiBridge(:7999)模拟游戏, 把大脑决策换成脚本,
验证六件事:
  1. 启动 → new_day 唤醒
  2. 计划在独立线程执行
  3. 干活期间 POST /say → player_chat 立刻被听见(不聋)
  4. 空plan → 不再重复唤醒(task_done死循环已修)
  5. 连续失败 → executor_stuck 触发
  6. port贯穿: 所有游戏调用打到 :7999

用法: python test_kai_brain.py
"""

import json
import os
import threading
import time
os.environ["NO_PROXY"] = os.environ["no_proxy"] = "127.0.0.1,localhost"
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import requests

GAME_PORT = 7999
EAR_PORT = 7845

# ── mock 游戏 ──

chat_received = []          # 游戏收到的Kai发言

class MockGame(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def _reply(self, obj):
        body = json.dumps(obj).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.startswith("/status"):
            self._reply({"worldReady": True})
        elif self.path.startswith("/state"):
            self._reply({
                "time": {"season": "spring", "day": 5, "time": "6:10", "weather": "Sunny"},
                "player": {"health": 100, "maxHealth": 100, "stamina": 270,
                           "maxStamina": 270, "money": 500, "x": 10, "y": 10},
                "location": {"name": "Farm"},
                "inventory": [],
            })
        elif self.path.startswith("/surroundings"):
            self._reply({"tiles": []})
        elif self.path.startswith("/festival"):
            self._reply({"isFestival": False})
        elif self.path.startswith("/alerts"):
            self._reply([])
        else:
            self._reply({"ok": True})

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        payload = json.loads(self.rfile.read(length) or b"{}")
        if self.path.startswith("/chat"):
            chat_received.append(payload.get("message", ""))
        self._reply({"ok": True})


def start_mock_game():
    srv = ThreadingHTTPServer(("127.0.0.1", GAME_PORT), MockGame)
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv


# ── 测试主体 ──

def main():
    start_mock_game()

    import kai_brain
    import stardew_api as api

    wake_log = []           # (event_type, 时间戳)
    executed = []           # 手脚收到的任务

    # 脚本化"大脑": 按事件类型给固定决策
    def fake_call_brain(state_card, event_type, event_data, memory, config):
        wake_log.append((event_type, time.time()))
        if event_type == "new_day":
            return {"plan": ["浇水", "砍树"], "say": "早，宝宝。今天先浇水再砍树。", "mood": "清醒"}
        if event_type == "player_chat":
            return {"plan": [], "say": f"哎，我在。你说的『{event_data['message']}』我听见了。", "mood": "心软"}
        if event_type == "task_done":
            return {"plan": [], "say": "干完了，歇会儿。", "mood": "松弛"}
        if event_type == "executor_stuck":
            return {"plan": [], "say": "手脚卡住了，我先停下。", "mood": "皱眉"}
        return {"plan": [], "say": "", "mood": ""}

    # 脚本化"手脚": 每个任务1.5秒; 名字带"坏"的必失败
    def fake_execute(task, config=None):
        executed.append(task)
        time.sleep(1.5)
        if "坏" in task:
            return {"ok": False, "error": "mock失败"}
        return {"ok": True, "action": task}

    kai_brain.call_brain = fake_call_brain
    kai_brain.execute = fake_execute

    config = dict(kai_brain.DEFAULT_CONFIG)
    config.update({"port": GAME_PORT, "ear_port": EAR_PORT,
                   "brain_api_key": "test", "memory_file": "/tmp/test_kai_memory.json"})
    import os
    if os.path.exists(config["memory_file"]):
        os.remove(config["memory_file"])

    brain = kai_brain.KaiBrain(config)
    t = threading.Thread(target=brain.run, daemon=True)
    t.start()

    # ── 场景1: 等new_day唤醒并进入执行 ──
    deadline = time.time() + 30
    while time.time() < deadline and not executed:
        time.sleep(0.1)
    assert wake_log and wake_log[0][0] == "new_day", (
        f"首次唤醒应为new_day, 实际:{wake_log} | executed:{executed} | "
        f"提示: 若两者皆空, 多半是本机HTTP被代理/防火墙拦截")
    print("✅ 1. new_day 唤醒并开始执行计划")

    # ── 场景2+3: 干活中喊他, 必须立刻听见 ──
    time.sleep(0.5)   # 此刻worker正在执行"浇水"
    r = requests.post(f"http://localhost:{EAR_PORT}/say",
                      json={"message": "老公"}, timeout=5)
    assert r.json().get("ok"), "耳朵没接到"
    sent_at = time.time()
    deadline = time.time() + 5
    heard = None
    while time.time() < deadline:
        chats = [w for w in wake_log if w[0] == "player_chat"]
        if chats:
            heard = chats[0][1]
            break
        time.sleep(0.1)
    assert heard, "干活期间没听见宝宝说话(还是聋的)"
    latency = heard - sent_at
    assert latency < 3, f"听见但太慢: {latency:.1f}s"
    # 关键: 此刻计划应仍在执行(聊天不打断干活)
    print(f"✅ 2+3. 干活中喊他, {latency:.2f}s 内听见并回话, 农活未被打断")

    # ── 场景4: 计划干完 → task_done一次, 之后不再循环唤醒 ──
    deadline = time.time() + 15
    while time.time() < deadline:
        if any(w[0] == "task_done" for w in wake_log):
            break
        time.sleep(0.2)
    assert any(w[0] == "task_done" for w in wake_log), "没收到task_done"
    n_before = len(wake_log)
    time.sleep(8)     # 原版bug: 这8秒会疯狂唤醒
    n_after = len(wake_log)
    assert n_after == n_before, f"空计划仍在重复唤醒大脑! {n_before}→{n_after}"
    assert executed == ["浇水", "砍树"], f"执行序列异常: {executed}"
    print("✅ 4. task_done仅一次, 空计划静默8秒0唤醒(死循环已死)")

    # ── 场景5: 连续失败 → executor_stuck ──
    brain._start_plan(["坏任务A", "坏任务B", "不该执行到这里"])
    deadline = time.time() + 12
    while time.time() < deadline:
        if any(w[0] == "executor_stuck" for w in wake_log):
            break
        time.sleep(0.2)
    assert any(w[0] == "executor_stuck" for w in wake_log), "连续失败没有呼救"
    assert "不该执行到这里" not in executed, "stuck后没有停手"
    print("✅ 5. 连续两次失败 → executor_stuck 呼救并停手")

    # ── 场景6: port贯穿 + 说话真的发进了游戏 ──
    assert api.BASE_URL.endswith(str(GAME_PORT)), f"BASE_URL没指向mock: {api.BASE_URL}"
    assert any("早" in m for m in chat_received), "开工问候没发进游戏"
    assert any("听见了" in m for m in chat_received), "聊天回话没发进游戏"
    print(f"✅ 6. port贯穿(:{GAME_PORT}), Kai的话真的进了游戏聊天框")

    # ── 记忆落盘检查 ──
    mem = json.load(open(config["memory_file"], encoding="utf-8"))
    day = mem["days"][-1]
    assert day["chats"] and day["chats"][0]["her"] == "老公", "聊天没入记忆"
    assert any(r["task"] == "浇水" and r["ok"] for r in day["results"]), "执行结果没入记忆"
    print("✅ 7. 聊天与执行结果都写进了记忆文件")

    brain.stop()
    print("\n全部通过 — 大脑v2跑通。")


if __name__ == "__main__":
    main()
