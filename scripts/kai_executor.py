"""
KaiFoxHollow — 手脚执行层
接收大脑的高层指令，拆解成NagiBridge API调用序列。
大部分操作走纯Python脚本（零token），复杂情况才用轻量模型。
"""

import os
import sys
import json
import time
import subprocess
import requests

import stardew_api as api

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))


# ══════════════════════════════════════
#  纯脚本执行器（零token消耗）
# ══════════════════════════════════════

SCRIPT_TASKS = {
    "浇水": ("water_crops.py", ""),
    "种田": ("farm_row.py", ""),
    "收割": ("harvest.py", ""),
    "砍树": ("chop_trees.py", ""),
    "开垦": ("clear_area.py", ""),
    "挖矿": ("mine_run.py", ""),
    "撸动物": ("pet_animals.py", ""),
    "酿酒": ("keg_manager.py", ""),
    "熔炉": ("furnace_manager.py", ""),
    "钓鱼": ("fish_run.py", ""),
}


def run_script(script_name, extra_args="", port=7842):
    """执行一个现成的自动化脚本。不走shell, 参数以列表传递。"""
    script_path = os.path.join(SCRIPT_DIR, script_name)
    if not os.path.exists(script_path):
        return {"ok": False, "error": f"脚本不存在: {script_name}"}

    cmd = [sys.executable, script_path]
    if extra_args:
        cmd += extra_args.split()
    cmd += ["--port", str(port)]
    env = {**os.environ,
           "PYTHONIOENCODING": "utf-8",
           "NO_PROXY": "127.0.0.1,localhost", "no_proxy": "127.0.0.1,localhost",
           "NAGI_URL": f"http://127.0.0.1:{port}"}
    try:
        result = subprocess.run(
            cmd, capture_output=True, text=True, timeout=300, env=env
        )
        return {
            "ok": result.returncode == 0,
            "stdout": result.stdout[-500:] if result.stdout else "",
            "stderr": result.stderr[-200:] if result.stderr else "",
        }
    except subprocess.TimeoutExpired:
        return {"ok": False, "error": "脚本超时(5分钟)"}


# ══════════════════════════════════════
#  简单指令直接执行（零token）
# ══════════════════════════════════════

NEGATION_WORDS = ("不", "别", "没", "勿", "停", "免")

def _negated(instruction, keyword):
    """关键词紧邻的前3个字里出现否定词, 视为反话, 跳过。
    例: '不用浇水' '别去挖矿' '今天没浇水的必要'"""
    idx = instruction.find(keyword)
    if idx <= 0:
        return False
    prefix = instruction[max(0, idx - 3):idx]
    return any(n in prefix for n in NEGATION_WORDS)


def execute_simple(instruction, port=7842):
    """
    尝试将大脑的自然语言指令匹配到纯脚本或简单API调用。
    返回 (handled: bool, result: dict)
    """
    instruction = instruction.strip()

    # 说话优先: 防止 '说:今天不挖矿了' 被任务词劫持
    if instruction.startswith("说:") or instruction.startswith("说："):
        msg = instruction.split(":", 1)[-1].split("：", 1)[-1].strip()
        api.chat(msg)
        return True, {"ok": True, "action": f"chat: {msg}"}

    # 匹配脚本任务(带否定检测)
    for keyword, (script, default_args) in SCRIPT_TASKS.items():
        if keyword in instruction and not _negated(instruction, keyword):
            result = run_script(script, default_args, port=port)
            return True, result

    # 简单动作
    if "回家" in instruction or "回农场" in instruction:
        api.warp("Farm")
        return True, {"ok": True, "action": "warp Farm"}

    if "去镇上" in instruction or "去小镇" in instruction:
        api.warp("Town")
        return True, {"ok": True, "action": "warp Town"}

    if "去海滩" in instruction:
        api.warp("Beach")
        return True, {"ok": True, "action": "warp Beach"}

    if "去矿洞" in instruction or "去矿井" in instruction:
        api.warp("Mine")
        return True, {"ok": True, "action": "warp Mine"}

    if "睡觉" in instruction or "上床" in instruction:
        api.warp("FarmHouse")
        time.sleep(1)
        result = requests.post(f"{api.BASE_URL}/sleep", json={}, timeout=10).json()
        return True, {"ok": True, "action": "sleep"}

    if "卖东西" in instruction or "出货" in instruction:
        api.sell(sell_all=True)
        return True, {"ok": True, "action": "sell all"}

    return False, {}


# ══════════════════════════════════════
#  轻量模型执行器（低成本）
# ══════════════════════════════════════

def execute_with_light_model(instruction, api_key, provider="deepseek",
                              model=None, port=7842, base_url=""):
    """
    用便宜模型把复杂指令拆解成API调用序列。
    只在 execute_simple 处理不了的时候才走这里。
    """
    from tool_agent import (
        TOOLS, execute_tool, call_openai_compatible, call_claude,
        convert_tools_openai, convert_tools_claude
    )

    default_models = {
        "deepseek": "deepseek-chat",
        "openai": "gpt-4o-mini",
        "claude": "claude-haiku-4-5-20251001",
    }
    model = model or default_models.get(provider, "deepseek-chat")

    system = (
        "你是一个星露谷物语的操作执行器。"
        "收到一条任务指令后，用工具完成它。"
        "不要聊天，不要解释，只执行。"
        "完成后回复'完成'和简要结果。"
    )

    messages = [
        {"role": "system", "content": system},
        {"role": "user", "content": f"当前状态请用get_state查看。任务: {instruction}"},
    ]

    max_turns = 8
    for _ in range(max_turns):
        if provider == "claude":
            # 直接调Haiku
            body = {
                "model": model,
                "max_tokens": 512,
                "system": system,
                "messages": [m for m in messages if m["role"] != "system"],
                "tools": convert_tools_claude(),
            }
            headers = {
                "Content-Type": "application/json",
                "x-api-key": api_key,
                "anthropic-version": "2023-06-01",
            }
            r = requests.post(
                "https://api.anthropic.com/v1/messages",
                json=body, headers=headers, timeout=60
            )
            if r.status_code != 200:
                return {"ok": False, "error": f"API {r.status_code}"}

            data = r.json()
            text = ""
            tool_calls = []
            for block in data.get("content", []):
                if block["type"] == "text":
                    text += block["text"]
                elif block["type"] == "tool_use":
                    tool_calls.append({
                        "id": block["id"],
                        "name": block["name"],
                        "arguments": block["input"],
                    })

            if not tool_calls:
                return {"ok": True, "result": text}

            # 执行工具
            content_blocks = []
            if text:
                content_blocks.append({"type": "text", "text": text})
            for tc in tool_calls:
                content_blocks.append({
                    "type": "tool_use", "id": tc["id"],
                    "name": tc["name"], "input": tc["arguments"]
                })
            messages.append({"role": "assistant", "content": content_blocks})

            for tc in tool_calls:
                result = execute_tool(tc["name"], tc["arguments"])
                result_str = json.dumps(result, ensure_ascii=False)[:1500]
                messages.append({
                    "role": "user",
                    "content": [{"type": "tool_result", "tool_use_id": tc["id"], "content": result_str}]
                })

        else:
            # OpenAI-compatible (DeepSeek, GPT)
            endpoints = {
                "deepseek": "https://api.deepseek.com/v1/chat/completions",
                "openai": "https://api.openai.com/v1/chat/completions",
            }
            endpoint = (base_url or "").strip() or endpoints.get(provider, endpoints["deepseek"])

            body = {
                "model": model,
                "max_tokens": 512,
                "messages": messages,
                "tools": convert_tools_openai(),
            }
            headers = {
                "Content-Type": "application/json",
                "Authorization": f"Bearer {api_key}",
            }
            r = requests.post(endpoint, json=body, headers=headers, timeout=60)
            if r.status_code != 200:
                return {"ok": False, "error": f"API {r.status_code}"}

            data = r.json()
            choice = data["choices"][0]
            msg = choice["message"]
            text = msg.get("content", "")
            tool_calls = []
            for tc in msg.get("tool_calls", []):
                tool_calls.append({
                    "id": tc["id"],
                    "name": tc["function"]["name"],
                    "arguments": json.loads(tc["function"]["arguments"]),
                })

            if not tool_calls:
                return {"ok": True, "result": text}

            assistant_msg = {"role": "assistant", "content": text or ""}
            if tool_calls:
                assistant_msg["tool_calls"] = [
                    {"id": tc["id"], "type": "function",
                     "function": {"name": tc["name"], "arguments": json.dumps(tc["arguments"])}}
                    for tc in tool_calls
                ]
            messages.append(assistant_msg)

            for tc in tool_calls:
                result = execute_tool(tc["name"], tc["arguments"])
                result_str = json.dumps(result, ensure_ascii=False)[:1500]
                messages.append({
                    "role": "tool", "tool_call_id": tc["id"], "content": result_str
                })

    return {"ok": True, "result": "执行完毕(达到最大轮次)"}


# ══════════════════════════════════════
#  统一入口
# ══════════════════════════════════════

def execute(instruction, config=None):
    """
    执行大脑发出的指令。
    先尝试纯脚本（零token），不行再用轻量模型。
    config: {"executor_api_key": str, "executor_provider": str, "executor_model": str, "port": int}
    """
    config = config or {}
    port = config.get("port", 7842)

    # 第一步：尝试纯脚本
    handled, result = execute_simple(instruction, port=port)
    if handled:
        return result

    # 第二步：用轻量模型
    api_key = config.get("executor_api_key", "")
    if not api_key:
        return {"ok": False, "error": "没有配置手脚模型的API key，且纯脚本无法处理此指令"}

    return execute_with_light_model(
        instruction,
        api_key=api_key,
        provider=config.get("executor_provider", "deepseek"),
        model=config.get("executor_model"),
        port=config.get("port", 7842),
        base_url=config.get("executor_base_url", ""),
    )
