"""
KaiFoxHollow — 状态卡片压缩器
把游戏完整状态压缩成一张几百token的卡片喂给大脑。
"""

import stardew_api as api


SEASON_CN = {"spring": "春", "summer": "夏", "fall": "秋", "winter": "冬"}
WEATHER_CN = {
    "Sun": "晴", "Sunny": "晴",
    "Rain": "雨", "Rainy": "雨",
    "Storm": "雷雨", "Stormy": "雷雨",
    "Snow": "雪", "Snowy": "雪",
    "Wind": "风", "Windy": "风", "Festival": "节日",
}


def compress_inventory(inventory, max_items=8):
    """只保留关键物品摘要，不列出全部背包。"""
    if not inventory:
        return "空"
    tools = []
    items = {}
    for item in inventory:
        name = item.get("name", "")
        if not name:
            continue
        if item.get("isTool"):
            tools.append(name)
        else:
            count = item.get("stack", 1)
            if name in items:
                items[name] += count
            else:
                items[name] = count

    parts = []
    if tools:
        parts.append(f"工具:{'/'.join(tools)}")

    # 按数量排序，取前几个
    sorted_items = sorted(items.items(), key=lambda x: -x[1])[:max_items]
    for name, count in sorted_items:
        parts.append(f"{name}x{count}")

    if len(items) > max_items:
        parts.append(f"...还有{len(items) - max_items}种")

    return " | ".join(parts)


def compress_surroundings(radius=10):
    """扫描周围，只返回有意义的信息。"""
    try:
        data = api.surroundings(radius)
    except Exception:
        return ""

    npcs = []
    crops_ready = 0
    crops_dry = 0
    objects_notable = []

    for tile in data.get("tiles", []):
        # NPC
        npc = tile.get("npc")
        if npc:
            npcs.append(f"{npc}({tile['x']},{tile['y']})")

        # 作物
        crop = tile.get("crop")
        if crop:
            if crop.get("harvestable"):
                crops_ready += 1
            if crop.get("needsWater"):
                crops_dry += 1

        # 有趣的物件
        obj = tile.get("object", "")
        if obj and obj not in ("Stone", "Weeds", "Twig"):
            if obj not in objects_notable:
                objects_notable.append(obj)

    parts = []
    if npcs:
        parts.append(f"附近NPC: {', '.join(npcs[:5])}")
    if crops_ready:
        parts.append(f"可收获作物: {crops_ready}个")
    if crops_dry:
        parts.append(f"需要浇水: {crops_dry}个")
    if objects_notable:
        parts.append(f"附近物件: {', '.join(objects_notable[:6])}")

    return " | ".join(parts)


def build_state_card():
    """
    生成一张状态卡片。目标：200-400 token。
    大脑每次被唤醒只看这张卡。
    """
    try:
        s = api.state()
    except Exception as e:
        return f"[游戏未连接: {e}]"

    player = s.get("player", {})
    location = s.get("location", {})
    time_info = s.get("time", {})

    def _fmt_tod(v):
        # 星露谷timeOfDay整数: 600=6:00, 1330=13:30, 2600=次日2:00
        try:
            v = int(v)
            h, m = divmod(v, 100)
            suffix = ""
            if h >= 24:
                h -= 24
                suffix = "(次日)"
            return f"{h}:{m:02d}{suffix}"
        except (TypeError, ValueError):
            return "?"


    # 基本信息
    season = SEASON_CN.get(time_info.get("season", ""), time_info.get("season", "?"))
    day = time_info.get("day") or time_info.get("dayOfMonth") or "?"
    time_str = time_info.get("time") or _fmt_tod(time_info.get("timeOfDay"))
    weather = WEATHER_CN.get(time_info.get("weather", ""), time_info.get("weather", "?"))

    # 宝宝在哪 — 这一行是他眼里最重要的一行
    farmers = s.get("farmers", []) or []
    if farmers:
        her = farmers[0]
        her_loc = her.get("location", "?")
        my_loc = s.get("location", {}).get("name", "?")
        if her_loc == my_loc:
            try:
                p = s.get("player", {})
                dist = abs(int(her.get("x", 0)) - int(p.get("x", 0))) + abs(int(her.get("y", 0)) - int(p.get("y", 0)))
                her_line = f"宝宝({her.get('name','?')}): 就在{her_loc}, 离你{dist}格"
            except (TypeError, ValueError):
                her_line = f"宝宝({her.get('name','?')}): 和你同在{her_loc}"
        else:
            her_line = f"宝宝({her.get('name','?')}): 在{her_loc}"
    else:
        her_line = "宝宝: 不在线"

    # 玩家状态
    health = player.get("health", 0)
    max_health = player.get("maxHealth", 100)
    stamina = int(player.get("stamina", 0))
    max_stamina = int(player.get("maxStamina", 270))
    gold = player.get("money", 0)
    px, py = player.get("x", 0), player.get("y", 0)
    loc_name = location.get("name", "?")

    # 背包
    inv_summary = compress_inventory(s.get("inventory", []))

    # 周围
    surr = compress_surroundings()

    card = f"""【{season}{day}日 {time_str} {weather}】
位置: {loc_name} ({px},{py})
体力: {stamina}/{max_stamina} | 生命: {health}/{max_health} | 金币: {gold}g
{her_line}
背包: {inv_summary}"""

    if surr:
        card += f"\n周围: {surr}"

    return card


def build_event_context(event_type, event_data=None):
    """
    为特定事件生成上下文补充。
    event_type: "new_day" | "player_chat" | "low_health" | "festival" | "executor_stuck"
    """
    ctx = ""

    if event_type == "she_arrived":
        ctx = "宝宝上线了, 她进入了农场。你想不想去找她, 或者先打个招呼。"
    elif event_type == "she_nearby":
        ctx = "宝宝就在你身边不远处。她走到你附近了——理她。"
    elif event_type == "missing_her":
        ctx = "你们好一阵子没说话了, 她还在线。想说什么就说。"
    elif event_type == "low_stamina":
        st = (event_data or {}).get("stamina", "?")
        ctx = f"体力快见底了(剩{st})。吃点东西能回体力(没有就想办法搞吃的), 或者收工。别硬撑。"
    elif event_type == "new_day":
        ctx = "新的一天开始了。决定今天的计划。"

    elif event_type == "player_chat":
        msg = event_data.get("message", "") if event_data else ""
        ctx = f"宝宝对你说: {msg}"

    elif event_type == "low_health":
        ctx = "生命值很低，需要决定是吃东西回血还是回家。"

    elif event_type == "festival":
        name = event_data.get("name", "未知节日") if event_data else "未知节日"
        ctx = f"今天是{name}！决定怎么参加。"

    elif event_type == "executor_stuck":
        reason = event_data.get("reason", "未知原因") if event_data else "未知原因"
        ctx = f"手脚执行出了问题: {reason}。需要大脑重新决策。"

    elif event_type == "task_done":
        task = event_data.get("task", "") if event_data else ""
        results = (event_data or {}).get("results", [])
        fails = [r["task"] for r in results if not r.get("ok")]
        ctx = f"刚刚完成了: {task}。"
        if fails:
            ctx += f" 其中失败: {'、'.join(fails)}。"
        ctx += "决定下一步做什么(也可以休息, 返回空plan)。"

    return ctx


if __name__ == "__main__":
    print(build_state_card())
