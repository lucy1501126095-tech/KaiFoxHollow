# NagiBridge — 星露谷打工指南

你是星露谷物语的农场打工仔。通过 NagiBridge HTTP API 控制角色。

## 你的端口
你控制端口 7842（host角色）。另一个AI（凪）控制端口 7843（farmhand）。不要动 7843。

## 项目位置
脚本在 scripts/ 目录下。所有 Python 脚本调用前必须加 PYTHONIOENCODING=utf-8。

## 可用脚本
- farm_row.py — 种田（翻地+播种+浇水）
- clear_area.py — 开垦（清杂草/石头/树）
- harvest.py — 收割（--sell 可出售）
- mine_run.py — 挖矿（未测试）
- stardew_api.py — API helper

所有脚本的 --port 参数用 7842。

## API 端点 (http://localhost:7842)
/status, /state, /move, /stop, /face, /select,
/use, /tool, /interact, /key, /chat, /emote,
/surroundings, /map, /warp, /sleep, /wakeup,
/queue, /buy, /harvest, /sell,
/give, /money, /ripen, /refill, /pause, /resume

## 踩坑记录（重要！）
1. farmhand 的 GetGrabTile 有偏移 → 统一站在目标格上方(y-1)面朝下操作
2. BeginUsingTool 有延迟，等几秒再检查结果
3. 浇水壶补水用 /refill 作弊端点，不要尝试对水源用工具
4. 收割前先检查背包空间，背包满了会收割失败
5. 放箱子用 /placechest，不要用 /give 刷箱子到背包

## 协作
操作前先读 scripts/coordination.json 看凪在干什么，避免重复。做完了更新你的状态。

## 挖矿
```bash
PYTHONIOENCODING=utf-8 python3 scripts/mine_run.py --start-level 1 --max-levels 5 --hp-threshold 30 --port 7842
```
- warp传送，不需要走路找矿洞入口
- AutoCombat mod 自动打怪，脚本只管敲石头
- 血量低或体力不足自动warp回Farm

## 重启游戏（加载新DLL时需要）
```powershell
# 1. 关掉游戏
Get-Process StardewModdingAPI -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3

# 2. 重新启动
Start-Process "E:\SteamLibrary\steamapps\common\Stardew Valley\StardewModdingAPI.exe"
```
启动后需要手动进存档（或等里奈操作），然后轮询等待 worldReady：
```bash
# 3. 等待游戏就绪
until curl -s http://localhost:7842/status 2>/dev/null | grep -q '"worldReady":true'; do sleep 5; done
echo "Game ready!"
```

## 卡住检测
每次操作后检查 /state 的 activeMenu 字段：
- `activeMenu` 不是 null → 有对话框/菜单弹出来了，你卡住了
- 用 `/key confirm` 推进对话
- 对话可能有多页，反复调 `/key confirm` 直到 activeMenu 变回 null

## git 同步
代码更新后先 `git pull`，如果 DLL 变了就重启游戏。

## 参考
完整技能文档见 scripts/SKILLS.md
