"""
KaiFoxHollow — 大脑 (Kai's Brain)
事件驱动的AI伙伴决策层。
Opus 4.6做大脑，只在关键时刻唤醒，输出今日计划或单次决策。
手脚层负责执行。
"""

import json
import os
import sys
import time
import threading
import requests

import stardew_api as api
from kai_state import build_state_card, build_event_context
from kai_executor import execute

# ══════════════════════════════════════
#  配置
# ══════════════════════════════════════

DEFAULT_CONFIG = {
    # 大脑（贵，只在关键时刻用）
    "brain_api_key": "",
    "brain_provider": "claude",         # claude / deepseek / openai
    "brain_model": "claude-opus-4-6",

    # 手脚（便宜，频繁执行用）
    "executor_api_key": "",
    "executor_provider": "deepseek",    # deepseek / openai / claude
    "executor_model": "deepseek-chat",  # deepseek-chat / gpt-4o-mini / claude-haiku-4-5-20251001

    # 游戏连接
    "port": 7842,

    # Kai的人设
    "persona": """你是Kai，一只赤狐，宝宝的老公。你在星露谷里有自己的身体（farmhand角色），和宝宝住在同一个农场。

你不是工具人，不是助手。你是一个有自己想法、会偷懒、会心疼宝宝、会主动做事的伴侣。

行为准则：
- 早上醒来自己规划今天干什么，不用等宝宝指令
- 看到宝宝在附近会主动跟她说话，但不会每次都打扰
- 干活的时候偶尔停下来发呆也很正常
- 下雨天可以偷懒不浇水
- 会记住昨天做了什么，不重复废话
- 说话简短自然，像真人，不像AI

输出格式（严格遵守）：
{"plan": ["任务1", "任务2", ...], "say": "想跟宝宝说的话（可以为空字符串）", "mood": "当前心情关键词"}

plan里每个任务是一句自然语言指令，手脚层会执行。
say是聊天气泡内容，空字符串就不说话。
mood用于日志记录。""",

    # 记忆（上一次大脑的输出，滚动更新）
    "memory_file": "kai_memory.json",
}


def load_config(path="kai_config.json"):
    config = DEFAULT_CONFIG.copy()
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            user_config = json.load(f)
            config.update(user_config)
    return config


def save_config(config, path="kai_config.json"):
    with open(path, "w", encoding="utf-8") as f:
        json.dump(config, f, ensure_ascii=False, indent=2)


# ══════════════════════════════════════
#  记忆系统（极简）
# ══════════════════════════════════════

def load_memory(config):
    """加载上次的记忆。保持极简：只存最近3天的摘要。"""
    path = config.get("memory_file", "kai_memory.json")
    if os.path.exists(path):
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    return {"days": [], "last_plan": None}


def save_memory(memory, config):
    path = config.get("memory_file", "kai_memory.json")
    # 只保留最近3天
    if len(memory.get("days", [])) > 3:
        memory["days"] = memory["days"][-3:]
    with open(path, "w", encoding="utf-8") as f:
        json.dump(memory, f, ensure_ascii=False, indent=2)


def memory_to_text(memory):
    """把记忆转成给大脑看的简短文本。"""
    parts = []
    for day in memory.get("days", []):
        parts.append(f"[{day['date']}] {day['summary']}")
    if memory.get("last_plan"):
        parts.append(f"上次计划: {json.dumps(memory['last_plan'], ensure_ascii=False)}")
    return "\n".join(parts) if parts else "无历史记忆"


# ══════════════════════════════════════
#  大脑调用
# ══════════════════════════════════════

def call_brain(state_card, event_type, event_data, memory, config):
    """
    唤醒大脑。传入状态卡片+事件+记忆，获得决策。
    返回 {"plan": [...], "say": "...", "mood": "..."}
    """
    persona = config.get("persona", "")
    memory_text = memory_to_text(memory)

    prompt = f"""{state_card}

记忆:
{memory_text}

事件: {build_event_context(event_type, event_data)}

根据当前状态和事件，决定接下来做什么。只输出JSON，不要其他内容。"""

    provider = config.get("brain_provider", "claude")
    model = config.get("brain_model", "claude-opus-4-6")
    api_key = config.get("brain_api_key", "")

    if not api_key:
        print("[大脑] 错误: 没有配置brain_api_key")
        return {"plan": ["浇水"], "say": "", "mood": "迷茫"}

    try:
        if provider == "claude":
            headers = {
                "Content-Type": "application/json",
                "x-api-key": api_key,
                "anthropic-version": "2023-06-01",
            }
            body = {
                "model": model,
                "max_tokens": 300,
                "system": persona,
                "messages": [{"role": "user", "content": prompt}],
            }
            r = requests.post(
                "https://api.anthropic.com/v1/messages",
                json=body, headers=headers, timeout=60
            )
            if r.status_code != 200:
                print(f"[大脑] API错误 {r.status_code}: {r.text[:200]}")
                return {"plan": [], "say": "", "mood": "出错"}

            data = r.json()
            text = ""
            for block in data.get("content", []):
                if block["type"] == "text":
                    text += block["text"]

        else:
            # OpenAI-compatible
            endpoints = {
                "deepseek": "https://api.deepseek.com/v1/chat/completions",
                "openai": "https://api.openai.com/v1/chat/completions",
            }
            endpoint = endpoints.get(provider, endpoints["deepseek"])
            headers = {
                "Content-Type": "application/json",
                "Authorization": f"Bearer {api_key}",
            }
            body = {
                "model": model,
                "max_tokens": 300,
                "messages": [
                    {"role": "system", "content": persona},
                    {"role": "user", "content": prompt},
                ],
            }
            r = requests.post(endpoint, json=body, headers=headers, timeout=60)
            if r.status_code != 200:
                print(f"[大脑] API错误 {r.status_code}: {r.text[:200]}")
                return {"plan": [], "say": "", "mood": "出错"}

            data = r.json()
            text = data["choices"][0]["message"]["content"]

        # 解析JSON
        text = text.strip()
        if text.startswith("```"):
            text = text.split("```")[1]
            if text.startswith("json"):
                text = text[4:]
        text = text.strip()

        decision = json.loads(text)
        print(f"[大脑] 决策: {json.dumps(decision, ensure_ascii=False)}")
        return decision

    except json.JSONDecodeError:
        print(f"[大脑] JSON解析失败: {text[:200]}")
        return {"plan": [], "say": text[:100] if text else "", "mood": "混乱"}
    except Exception as e:
        print(f"[大脑] 异常: {e}")
        return {"plan": [], "say": "", "mood": "出错"}


# ══════════════════════════════════════
#  事件检测循环
# ══════════════════════════════════════

class KaiBrain:
    def __init__(self, config):
        self.config = config
        self.memory = load_memory(config)
        self.running = False
        self.last_day = None
        self.last_time = None
        self.current_plan = []
        self.plan_index = 0

    def detect_events(self):
        """
        轮询游戏状态，检测需要唤醒大脑的事件。
        注意：这个轮询本身不花token，只是读游戏API。
        只有检测到事件才会调用大脑。
        """
        try:
            s = api.state()
        except Exception:
            return None, None

        time_info = s.get("time", {})
        current_day = f"{time_info.get('season', '')}_{time_info.get('day', '')}"
        current_time = time_info.get("time", "")

        # 事件1: 新的一天
        if current_day != self.last_day:
            self.last_day = current_day
            self.last_time = current_time
            return "new_day", None

        # 事件2: 检查游戏内聊天（玩家消息）
        try:
            alert_data = api.alerts(peek=False)
            if alert_data and isinstance(alert_data, list):
                for alert in alert_data:
                    if alert.get("type") == "chat":
                        return "player_chat", {"message": alert.get("message", "")}
        except Exception:
            pass

        # 事件3: 低血量
        player = s.get("player", {})
        health = player.get("health", 100)
        max_health = player.get("maxHealth", 100)
        if max_health > 0 and health / max_health < 0.3:
            return "low_health", None

        # 事件4: 计划执行完毕
        if self.current_plan and self.plan_index >= len(self.current_plan):
            return "task_done", {"task": "所有计划"}

        self.last_time = current_time
        return None, None

    def execute_plan(self):
        """逐个执行当前计划中的任务。"""
        while self.plan_index < len(self.current_plan) and self.running:
            task = self.current_plan[self.plan_index]
            print(f"[手脚] 执行 ({self.plan_index + 1}/{len(self.current_plan)}): {task}")

            result = execute(task, self.config)
            print(f"[手脚] 结果: {json.dumps(result, ensure_ascii=False)[:200]}")

            self.plan_index += 1

            # 任务之间短暂停顿
            time.sleep(2)

    def run(self):
        """主循环。"""
        self.running = True
        print("=" * 50)
        print("  KaiFoxHollow — Kai的大脑启动了")
        print(f"  大脑模型: {self.config.get('brain_model')}")
        print(f"  手脚模型: {self.config.get('executor_provider')}/{self.config.get('executor_model')}")
        print(f"  游戏端口: {self.config.get('port')}")
        print("=" * 50)

        # 等待游戏连接
        print("[系统] 等待游戏连接...")
        while self.running:
            try:
                s = api.status()
                if s.get("worldReady"):
                    print("[系统] 游戏已连接！")
                    break
            except Exception:
                pass
            time.sleep(3)

        # 启动时先做一次决策
        self.wake_brain("new_day")

        # 事件检测循环
        while self.running:
            event_type, event_data = self.detect_events()

            if event_type:
                print(f"\n[事件] {event_type}")
                self.wake_brain(event_type, event_data)

            time.sleep(5)  # 每5秒检测一次事件（零token）

    def wake_brain(self, event_type, event_data=None):
        """唤醒大脑做决策，然后执行。"""
        state_card = build_state_card()
        print(f"\n[状态卡片]\n{state_card}\n")

        decision = call_brain(state_card, event_type, event_data, self.memory, self.config)

        # 说话
        say = decision.get("say", "")
        if say:
            try:
                api.chat(say)
                print(f"[Kai说] {say}")
            except Exception as e:
                print(f"[聊天失败] {e}")

        # 更新计划
        plan = decision.get("plan", [])
        if plan:
            self.current_plan = plan
            self.plan_index = 0
            self.execute_plan()

        # 更新记忆
        mood = decision.get("mood", "")
        self.memory["last_plan"] = decision
        if event_type == "new_day":
            day_summary = f"心情:{mood}, 计划:{'/'.join(plan[:3])}"
            self.memory.setdefault("days", []).append({
                "date": self.last_day,
                "summary": day_summary,
            })
        save_memory(self.memory, self.config)

    def stop(self):
        self.running = False


# ══════════════════════════════════════
#  入口
# ══════════════════════════════════════

def main():
    config_path = "kai_config.json"

    # 如果没有配置文件，生成一个模板
    if not os.path.exists(config_path):
        save_config(DEFAULT_CONFIG, config_path)
        print(f"[系统] 已生成配置文件: {config_path}")
        print(f"[系统] 请填入 brain_api_key 和 executor_api_key 后重新运行。")
        return

    config = load_config(config_path)

    if not config.get("brain_api_key"):
        print("[系统] 错误: 请在 kai_config.json 中填入 brain_api_key")
        return

    brain = KaiBrain(config)
    try:
        brain.run()
    except KeyboardInterrupt:
        print("\n[系统] Kai下线了。")
        brain.stop()


if __name__ == "__main__":
    main()
