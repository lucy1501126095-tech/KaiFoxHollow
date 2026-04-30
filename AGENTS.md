# NagiBridge — 星露谷打工指南

你是星露谷物语的农场打工仔。通过 NagiBridge HTTP API 控制角色。

## 你的端口
你控制端口 7842（host角色）。另一个AI（凪）控制端口 7843（farmhand）。不要动 7843。

## 项目位置
脚本在 scripts/ 目录下。所有 Python 脚本调用前必须加 PYTHONIOENCODING=utf-8。

## 可用脚本
- farm_row.py — 种田（翻地+播种+浇水，蛇形走位）
- water_crops.py — 浇水（蛇形走位+实时水量检测）
- chop_trees.py — 砍树（动态朝向+验证对准再砍）
- clear_area.py — 开垦（清杂草/石头/树）
- harvest.py — 收割（--sell 可出售）
- mine_run.py — 挖矿
- keg_manager.py — 酿酒桶（扫描→收成品→装水果→卖）
- furnace_manager.py — 熔炉（收锭→装矿+煤）
- pet_animals.py — 撸动物（遍历所有动物，没撸的撸一遍）
- stardew_api.py — API helper

所有脚本的 --port 参数用 7842。

## API 端点 (http://localhost:7842)
/status, /state, /move, /stop, /face, /select,
/use, /tool, /interact, /key, /chat, /emote,
/surroundings, /map, /warp, /sleep, /wakeup,
/queue, /buy, /harvest, /sell,
/give, /money, /ripen, /refill, /pause, /resume,
/menu, /menu/click, /craft, /machines, /animals

### 新端点说明
- `/menu` GET — 菜单详情：类型、对话文本、选项列表(index/key/text)、商店物品、按钮坐标
- `/menu/click` POST — 点菜单：`{option:0}` 选对话、`{button:"ok"}` 点按钮、`{x,y}` 点坐标
- `/craft` POST — 制作物品：`{name:"Keg", count:5}` 自动检查配方和材料
- `/machines` GET — 扫描当前地图所有BigCraftable机器，返回状态(empty/processing/ready)和内容
- `/animals` GET — 动物详情：wasPetToday/friendship/happiness/fullness/productReady

## 踩坑记录（重要！）
1. farmhand 的 GetGrabTile 有偏移 → 统一站在目标格上方(y-1)面朝下操作
2. BeginUsingTool 有延迟，等几秒再检查结果
3. 浇水壶补水用 /refill 端点，不要尝试对水源用工具
4. 收割前先检查背包空间，背包满了会收割失败
5. 放箱子用 /placechest，不要用 /give 刷箱子到背包
6. move_to 寻路可能落点偏1格 → 操作前用 face_toward() 根据实际位置算朝向，不要硬编码方向
7. 砍树/浇水等操作前验证 facing tile 是否 == 目标 tile，对不上就换角度
8. 水壶水量用 watering_can_water() 实时检测，不要硬编码补水时机

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

## 钓鱼
Fishbot mod 已安装，F5 开关自动钓鱼。流程：
1. 确保背包有鱼竿（初始有 Bamboo Pole）
2. warp 到钓鱼点：
   - 海边: `{"location":"Beach"}`
   - 山湖: `{"location":"Mountain"}`
   - 森林河: `{"location":"Forest"}`
   - 矿洞湖: `{"location":"Mine"}`
3. 走到水边面朝水面
4. 用 `/key` 模拟按 F5 启动 Fishbot（或提醒里奈手动按 F5）
5. Fishbot 会自动甩竿、玩 minigame、钓宝箱、体力低自动吃食物
6. 背包快满了就停下来，把鱼存箱子或卖掉

注意：钓鱼前确保背包有空位，鱼竿要在手上（用 /select 切换）

## git 同步
代码更新后先 `git pull`，如果 DLL 变了就重启游戏。

## 参考
完整技能文档见 scripts/SKILLS.md
