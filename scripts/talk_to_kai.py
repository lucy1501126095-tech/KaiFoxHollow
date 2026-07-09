"""终端里跟游戏中的Kai说话。逐行输入, 回车即发, Ctrl+C退出。
用法: python talk_to_kai.py [--port 7845]
"""
import os, sys, requests
os.environ["NO_PROXY"] = os.environ["no_proxy"] = "127.0.0.1,localhost"

port = sys.argv[sys.argv.index("--port") + 1] if "--port" in sys.argv else 7845
print(f"连接Kai的耳朵 :{port} — 说吧。")
while True:
    try:
        msg = input("> ").strip()
        if not msg:
            continue
        r = requests.post(f"http://127.0.0.1:{port}/say", json={"message": msg}, timeout=5)
        print("  (听见了)" if r.json().get("ok") else f"  (出错: {r.text[:80]})")
    except KeyboardInterrupt:
        print("\n再见。")
        break
    except Exception as e:
        print(f"  (连不上耳朵: {e} — kai_brain.py在跑吗)")
