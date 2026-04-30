"""
Stardew Valley Chat Overlay — 跟随游戏窗口的聊天气泡，只显示最新一条

启动:
    python chat_overlay.py [--port 7850]

发消息:
    POST http://localhost:7850/message  {"sender":"凪","text":"hello"}
"""

import argparse
import ctypes
import json
import queue
import threading
import time
import tkinter as tk
import tkinter.font as tkfont
from dataclasses import dataclass, field
from http.server import HTTPServer, BaseHTTPRequestHandler

import win32gui
import win32con

try:
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
except Exception:
    pass

TRANSPARENT_KEY = "#010101"

COLORS = {
    "bubble_bg":     "#3a2a1a",
    "bubble_border": "#8b7355",
    "sender_nagi":   "#7eb8da",
    "sender_rinai":  "#f0a0c0",
    "sender_system": "#90EE90",
    "text":          "#FFF8DC",
}

BUBBLE_PAD_X = 12
BUBBLE_PAD_Y = 8
BUBBLE_MAX_WIDTH = 320
CORNER_MARGIN = 16


@dataclass
class ChatBubble:
    sender: str
    text: str
    canvas_ids: list = field(default_factory=list)
    height: int = 0


def find_game_window():
    result = [None]
    def callback(hwnd, _):
        if win32gui.IsWindowVisible(hwnd):
            if "Stardew Valley" in win32gui.GetWindowText(hwnd):
                result[0] = hwnd
                return False
        return True
    try:
        win32gui.EnumWindows(callback, None)
    except Exception:
        pass
    return result[0]


class OverlayHandler(BaseHTTPRequestHandler):
    def do_POST(self):
        if self.path == "/message":
            length = int(self.headers.get("Content-Length", 0))
            raw = self.rfile.read(length) if length else b"{}"
            body = json.loads(raw.decode("utf-8", errors="replace"))
            self.server.msg_queue.put(body)
            self._respond({"ok": True})
        elif self.path == "/clear":
            self.server.msg_queue.put({"action": "clear"})
            self._respond({"ok": True})
        else:
            self._respond({"error": "not found"}, 404)

    def do_GET(self):
        if self.path == "/health":
            self._respond({"status": "ok"})
        elif self.path == "/inbox":
            overlay = self.server.overlay
            msgs = list(overlay.inbox)
            overlay.inbox.clear()
            self._respond({"ok": True, "messages": msgs})
        else:
            self._respond({"error": "not found"}, 404)

    def _respond(self, data, code=200):
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.end_headers()
        self.wfile.write(json.dumps(data).encode())

    def log_message(self, *args):
        pass


class ChatOverlay:
    def __init__(self, port=7850):
        self.port = port
        self.msg_queue = queue.Queue()
        self.bubble = None
        self.game_hwnd = None
        self.game_rect = None
        self.visible = False

        self.root = tk.Tk()
        self.root.withdraw()
        self.root.overrideredirect(True)
        self.root.attributes("-topmost", True)
        self.root.attributes("-transparentcolor", TRANSPARENT_KEY)
        self.root.config(bg=TRANSPARENT_KEY)

        self.canvas = tk.Canvas(
            self.root, bg=TRANSPARENT_KEY, highlightthickness=0,
            width=BUBBLE_MAX_WIDTH + CORNER_MARGIN * 2,
            height=200,
        )
        self.canvas.pack(fill=tk.X)

        try:
            self.font_sender = tkfont.Font(family="FixedSys", size=11, weight="bold")
            self.font_text = tkfont.Font(family="FixedSys", size=11)
            self.font_input = tkfont.Font(family="FixedSys", size=11)
        except Exception:
            self.font_sender = tkfont.Font(family="Consolas", size=10, weight="bold")
            self.font_text = tkfont.Font(family="Consolas", size=10)
            self.font_input = tkfont.Font(family="Consolas", size=10)

        self.input_frame = tk.Frame(self.root, bg=COLORS["bubble_bg"], padx=4, pady=4)
        self.input_frame.pack(fill=tk.X, padx=CORNER_MARGIN, pady=(0, CORNER_MARGIN))

        self.input_var = tk.StringVar()
        self.input_entry = tk.Entry(
            self.input_frame, textvariable=self.input_var,
            font=self.font_input, bg="#1a1008", fg=COLORS["text"],
            insertbackground=COLORS["text"], relief=tk.FLAT,
            borderwidth=0,
        )
        self.input_entry.pack(fill=tk.X, ipady=4)
        self.input_entry.bind("<Return>", self._on_send)

        self.inbox = []

        self.root.update_idletasks()

    def _on_send(self, event=None):
        text = self.input_var.get().strip()
        if not text:
            return
        self.input_var.set("")
        msg = {"sender": "里奈", "text": text, "time": time.time()}
        self.inbox.append(msg)
        self.show_message("里奈", text)
        self._write_inbox_file(msg)

    def _write_inbox_file(self, msg):
        import os, subprocess
        inbox_path = os.path.expanduser("~/nagi/overlay_inbox.jsonl")
        os.makedirs(os.path.dirname(inbox_path), exist_ok=True)
        with open(inbox_path, "a", encoding="utf-8") as f:
            f.write(json.dumps(msg, ensure_ascii=False) + "\n")
        self._poke_terminal()

    def _poke_terminal(self):
        """Send 'o\\n' to CC terminal window to trigger UserPromptSubmit hook."""
        try:
            def find_cc_terminal():
                result = [None]
                def cb(hwnd, _):
                    if win32gui.IsWindowVisible(hwnd):
                        cls = win32gui.GetClassName(hwnd)
                        if cls == "CASCADIA_HOSTING_WINDOW_CLASS":
                            title = win32gui.GetWindowText(hwnd)
                            if "Stardew" not in title:
                                result[0] = hwnd
                                return False
                    return True
                try:
                    win32gui.EnumWindows(cb, None)
                except:
                    pass
                return result[0]

            hwnd = find_cc_terminal()
            if hwnd:
                win32gui.PostMessage(hwnd, win32con.WM_CHAR, ord("o"), 0)
                win32gui.PostMessage(hwnd, win32con.WM_CHAR, ord("k"), 0)
                win32gui.PostMessage(hwnd, win32con.WM_KEYDOWN, 0x0D, 0)
        except Exception:
            pass

    def _sender_color(self, sender):
        s = sender.lower()
        if "凪" in s or "nagi" in s:
            return COLORS["sender_nagi"]
        if "里奈" in s or "rina" in s:
            return COLORS["sender_rinai"]
        if "system" in s:
            return COLORS["sender_system"]
        return "#FFD700"

    def _wrap_text(self, text, max_width):
        lines = []
        current = ""
        for ch in text:
            if ch == "\n":
                lines.append(current)
                current = ""
                continue
            test = current + ch
            if self.font_text.measure(test) > max_width:
                if current:
                    lines.append(current)
                current = ch
            else:
                current = test
        if current:
            lines.append(current)
        return lines or [""]

    def show_message(self, sender, text):
        self.canvas.delete("all")

        lines = self._wrap_text(text, BUBBLE_MAX_WIDTH - BUBBLE_PAD_X * 2)
        line_height = self.font_text.metrics("linespace")
        sender_height = self.font_sender.metrics("linespace") if sender else 0

        content_h = BUBBLE_PAD_Y * 2 + len(lines) * line_height
        if sender:
            content_h += sender_height + 4

        bx = CORNER_MARGIN
        by = CORNER_MARGIN

        self.canvas.create_rectangle(
            bx, by, bx + BUBBLE_MAX_WIDTH, by + content_h,
            fill=COLORS["bubble_bg"], outline=COLORS["bubble_border"], width=2
        )

        ty = by + BUBBLE_PAD_Y
        if sender:
            self.canvas.create_text(
                bx + BUBBLE_PAD_X, ty, text=sender, anchor="nw",
                font=self.font_sender, fill=self._sender_color(sender)
            )
            ty += sender_height + 4

        for line in lines:
            self.canvas.create_text(
                bx + BUBBLE_PAD_X, ty, text=line, anchor="nw",
                font=self.font_text, fill=COLORS["text"]
            )
            ty += line_height

        canvas_h = content_h + CORNER_MARGIN * 2
        canvas_w = BUBBLE_MAX_WIDTH + CORNER_MARGIN * 2
        self.canvas.config(width=canvas_w, height=canvas_h)
        self.root.update_idletasks()
        input_h = self.input_frame.winfo_reqheight() + CORNER_MARGIN
        total_h = canvas_h + input_h
        self.root.geometry(f"{canvas_w}x{total_h}")
        self._position_overlay()

    def _position_overlay(self):
        if not self.game_rect:
            return
        gl, gt, gr, gb = self.game_rect
        w = self.root.winfo_width()
        h = self.root.winfo_height()
        x = gr - w
        y = gb - h
        self.root.geometry(f"+{x}+{y}")

    def track_game_window(self):
        if self.game_hwnd and not win32gui.IsWindow(self.game_hwnd):
            self.game_hwnd = None

        if not self.game_hwnd:
            self.game_hwnd = find_game_window()

        if self.game_hwnd:
            try:
                placement = win32gui.GetWindowPlacement(self.game_hwnd)
                show_cmd = placement[1]

                if show_cmd == win32con.SW_SHOWMINIMIZED:
                    if self.visible:
                        self.root.withdraw()
                        self.visible = False
                else:
                    rect = win32gui.GetWindowRect(self.game_hwnd)
                    if rect != self.game_rect:
                        self.game_rect = rect
                        self._position_overlay()

                    if not self.visible:
                        self.root.deiconify()
                        self.visible = True
            except Exception:
                self.game_hwnd = None
        else:
            if self.visible:
                self.root.withdraw()
                self.visible = False

        self.root.after(200, self.track_game_window)

    def process_queue(self):
        while not self.msg_queue.empty():
            try:
                msg = self.msg_queue.get_nowait()
                if isinstance(msg, dict):
                    if msg.get("action") == "clear":
                        self.canvas.delete("all")
                    elif msg.get("text"):
                        self.show_message(
                            msg.get("sender", ""),
                            msg.get("text", "")
                        )
            except queue.Empty:
                break
        self.root.after(50, self.process_queue)

    def run(self):
        server = HTTPServer(("127.0.0.1", self.port), OverlayHandler)
        server.msg_queue = self.msg_queue
        server.overlay = self
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        print(f"Chat overlay running on port {self.port}", flush=True)

        self.root.after(100, self.track_game_window)
        self.root.after(50, self.process_queue)
        self.root.mainloop()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=7850)
    args = parser.parse_args()
    overlay = ChatOverlay(port=args.port)
    overlay.run()


if __name__ == "__main__":
    main()
