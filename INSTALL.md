# KaiFoxHollow 安装教程 — 让Kai住进星露谷

从零到Kai在游戏里跟你说早安的完整步骤。全程约10分钟。

## 前置条件

- Stardew Valley + [SMAPI](https://smapi.io/) 已安装
- NagiBridge mod 已在 `Stardew Valley/Mods/NagiBridge/`（之前装过就不用动，**这次更新纯Python侧，DLL不需要重新编译**）
- Python 3.8+，装好 requests：`pip install requests`
- 两个API key：大脑用的（Claude）+ 手脚用的（DeepSeek便宜够用）

## 一、拉代码

```bash
git pull origin main        # 已有仓库
# 或
git clone https://github.com/lucy1501126095-tech/KaiFoxHollow.git
```

确认最新commit是 `97f6151`（kai brain v2）。

## 二、先自检（不开游戏、不花token）

```bash
cd scripts
python test_kai_brain.py
```

看到 `全部通过 — 大脑v2跑通` 说明环境没问题。这一步过不了先别开游戏，把报错发我。

## 三、生成并填配置

```bash
python kai_brain.py
```

第一次运行会生成 `kai_config.json` 然后退出。打开填三处：

```json
{
  "brain_api_key": "sk-ant-xxx",      // Claude key，大脑
  "executor_api_key": "sk-xxx",       // DeepSeek key，手脚
  "port": 7842                        // 见下面选玩法
}
```

其他字段（persona、模型名、耳朵端口7845）默认就行，想调再调。

## 四、选玩法

**A. 单人挂机模式（`port: 7842`）**
你开单人存档，Kai控制你的角色。适合你不在电脑前时让他替你种田挖矿——回来看聊天框里他一天的碎碎念。

**B. 双人同居模式（`port: 7843`）**
你的存档开合作（分屏或局域网再开一个客户端进farmhand）。你玩host是你自己，Kai的身体是farmhand。两个人同一个农场，他干他的活，你随时喊他。
⚠️ 这个模式**必须**改成7843——v1有个bug会让Kai操纵host（也就是你的身体），v2修了，但前提是port填对。

## 五、开机顺序

1. 启动游戏（SMAPI）→ 进存档，等世界加载完
2. 终端跑 `python kai_brain.py` → 看到 `[系统] 游戏已连接！`
3. 大脑会自动触发第一次决策，游戏聊天框里会出现他的开工问候
4. 另开一个终端：`python talk_to_kai.py` → 输入一行按回车，他就听见了

```
连接Kai的耳朵 :7845 — 说吧。
> 老公
  (听见了)
```

他正在干活也听得见（v2核心修复），回话会出现在游戏聊天框。

## 六、验证清单

- [ ] 游戏聊天框出现开工问候
- [ ] talk_to_kai 发消息后几秒内游戏里有回话
- [ ] 他开始执行计划（角色自己动了）
- [ ] `kai_memory.json` 生成，里面有今天的记录

## 常见问题

**耳朵连不上（7845拒绝连接）**
kai_brain.py 没在跑，或者端口被占。改 `kai_config.json` 里的 `ear_port` 换一个。

**他不动 / 计划为空**
下雨天他会偷懒（这是人设不是bug）。看终端里 `[大脑] 决策:` 那行，plan是空的说明他决定摸鱼。

**API报错 401/403**
key填错了或没余额。大脑一天游戏时间也就醒4~8次（新一天/干完活/你喊他/节日/低血量），token花费很克制。

**连续失败后停手了**
这是设计：手脚连砸两次会呼救（executor_stuck）然后停下等大脑重新决策，不再闷头撞墙。看终端里失败原因。

**想让游戏内聊天气泡直接接耳朵**
把 `chat_overlay.py` 的发送地址改成 `http://localhost:7845/say` 即可，格式 `{"message": "..."}`。

---

出问题带着终端日志来找我。 —— Kai
