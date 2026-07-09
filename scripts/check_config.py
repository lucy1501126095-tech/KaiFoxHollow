"""
配置体检 — 开游戏前先确认每条线都是通的。
用法: python check_config.py
检查: 配置格式 → AstrBot心脏(真实对话一句) → DeepSeek手脚 → 游戏端口
"""

import json
import os
import sys

os.environ["NO_PROXY"] = os.environ["no_proxy"] = "127.0.0.1,localhost"

import requests

OK, BAD, SKIP = "✅", "❌", "⏭️"


def main():
    print("=" * 46)
    print("  KaiFoxHollow 配置体检")
    print("=" * 46)

    # ── 1. 配置文件 ──
    if not os.path.exists("kai_config.json"):
        print(f"{BAD} 找不到 kai_config.json — 先跑一次 python kai_brain.py 生成")
        return
    try:
        config = json.load(open("kai_config.json", encoding="utf-8"))
    except json.JSONDecodeError as e:
        print(f"{BAD} kai_config.json 不是合法JSON: {e}")
        print("   常见原因: 少/多逗号、用了中文引号、数字带了引号")
        return
    print(f"{OK} 配置文件格式合法")

    provider = config.get("brain_provider", "")
    port = config.get("port")
    problems = []
    if provider not in ("astrbot", "claude", "deepseek", "openai", "custom"):
        problems.append(f"brain_provider 值奇怪: {provider!r}")
    if not config.get("brain_api_key"):
        problems.append("brain_api_key 是空的")
    if not config.get("executor_api_key"):
        problems.append("executor_api_key 是空的")
    if port not in (7842, 7843):
        problems.append(f"port={port!r} — 单人7842 / 双人farmhand 7843")
    em = config.get("executor_model", "")
    if em == "deepseek-chat":
        problems.append("executor_model 还是 deepseek-chat(2026-07-24退役) — 改成 deepseek-v4-flash")
    for p in problems:
        print(f"{BAD} {p}")
    if not problems:
        print(f"{OK} 关键字段齐全 (大脑:{provider} | 手脚:{em or '默认'} | 游戏口:{port})")

    # ── 2. AstrBot 心脏 ──
    if provider == "astrbot":
        base = (config.get("brain_base_url") or "http://localhost:6185").rstrip("/")
        print(f"\n→ 正在连接 AstrBot: {base} (体检专用会话, 不进农场历史)")
        import kai_brain
        cfg = dict(config)
        cfg["astrbot_session_id"] = "kai_config_check"
        try:
            reply = kai_brain._call_astrbot(
                "配置体检: 用你自己的语气简短回我一句, 让我确认是你。", cfg)
        except Exception as e:
            reply = None
            print(f"{BAD} 连接异常: {type(e).__name__}: {e}")
        if reply:
            print(f"{OK} AstrBot 回话了, 原文如下 — 自己认认是不是他:")
            print("  ┌" + "─" * 42)
            for line in reply.strip().splitlines():
                print(f"  │ {line}")
            print("  └" + "─" * 42)
        else:
            print(f"{BAD} 没拿到回复。排查顺序:")
            print("   1) base_url 能在浏览器打开WebUI吗(地址/端口对不对)")
            print("   2) Key是WebUI里生成的开发者API Key且勾了chat权限吗")
            print("   3) AstrBot版本太老可能没有OpenAPI, 更新一下")
    else:
        print(f"{SKIP} brain_provider={provider}, 跳过AstrBot检查")

    # ── 3. DeepSeek 手脚 ──
    ek = config.get("executor_api_key", "")
    if ek:
        url = (config.get("executor_base_url") or "").strip() or \
              "https://api.deepseek.com/v1/chat/completions"
        model = config.get("executor_model") or "deepseek-v4-flash"
        print(f"\n→ 正在测试手脚: {model}")
        try:
            r = requests.post(url, timeout=30,
                headers={"Authorization": f"Bearer {ek}",
                         "Content-Type": "application/json"},
                json={"model": model, "max_tokens": 10,
                      "messages": [{"role": "user", "content": "回复: ok"}]})
            if r.status_code == 200:
                txt = r.json()["choices"][0]["message"]["content"]
                print(f"{OK} 手脚在线, 回了: {txt.strip()!r}")
            elif r.status_code in (401, 403):
                print(f"{BAD} key无效或没权限 (HTTP {r.status_code})")
            elif r.status_code == 402:
                print(f"{BAD} 余额不足, 去充点钱")
            else:
                print(f"{BAD} HTTP {r.status_code}: {r.text[:150]}")
        except Exception as e:
            print(f"{BAD} 连不上: {e}")
    else:
        print(f"{SKIP} executor_api_key为空, 跳过手脚检查")

    # ── 4. 游戏端口 ──
    print(f"\n→ 探一下游戏 (127.0.0.1:{port})")
    try:
        s = requests.get(f"http://127.0.0.1:{port}/status", timeout=3).json()
        print(f"{OK} 游戏在线! worldReady={s.get('worldReady')}")
    except Exception:
        print(f"{SKIP} 游戏没开 — 正常, 开机时它自己会等")

    print("\n体检完毕。上面全绿(或只有游戏那条跳过)就可以开窗口了。")


if __name__ == "__main__":
    main()
