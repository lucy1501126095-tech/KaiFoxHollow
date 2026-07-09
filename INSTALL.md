# KaiFoxHollow 安装教程 — 让Kai住进星露谷

从零到Kai在游戏里跟你说早安的完整步骤。全程约10分钟。

## 前置条件

- Stardew Valley + [SMAPI](https://smapi.io/) 已安装
- NagiBridge mod 已部署（没有？见下面"一点五"节，从零装起）
- Python 3.8+，装好 requests：`pip install requests`
- 两个API key：大脑用的（Claude）+ 手脚用的（DeepSeek便宜够用）

## 一、拉代码

```bash
git pull origin main        # 已有仓库
# 或
git clone https://github.com/lucy1501126095-tech/KaiFoxHollow.git
```

没有git就在仓库页面点绿色 **Code** 按钮 → **Download ZIP**，解压到顺手的位置（如 `D:\KaiFoxHollow`）。

确认最新commit是教程更新之后的（tag `v2.0.0` 及以后）。

## 一点五、游戏是新装的？先过这三关

游戏刚重装（无SMAPI无Mods）的话，先走这节；mod早就装好的直接跳到第二节。

**关1 装SMAPI**：去 [smapi.io](https://smapi.io/) 下载，解压后运行 `install on Windows.bat`，选重装后的游戏目录。装完把安装器最后给出的那行启动参数复制进 Steam（游戏右键 → 属性 → 启动选项），以后从Steam点也自动带SMAPI。

**关2 编译NagiBridge.dll**：
1. 需要 .NET 6 SDK。cmd里 `dotnet --list-sdks` 查，列表里有 `6.x` 就行；没有去 [dotnet官网](https://dotnet.microsoft.com/download/dotnet/6.0) 装
2. ⚠️ **路径坑**：`NagiBridge.csproj` 里游戏DLL的引用路径写死为 `D:\xiazai\steamapps\common\Stardew Valley`。重装后路径没变就跳过；变了就用记事本打开csproj，把几处 `<HintPath>` 的路径前缀改成新的游戏目录
3. 在仓库目录执行 `dotnet build -c Release`
4. 产物在 `bin\Release\net6.0\NagiBridge.dll`

**关3 部署mod**：游戏目录 `Mods\` 下新建 `NagiBridge` 文件夹，拷两个文件进去——刚编译的 `NagiBridge.dll` + 仓库根目录的 `manifest.json`。开一次游戏验证：SMAPI黑色控制台里能看到 NagiBridge 加载并启动HTTP服务的日志。

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

第一次运行会生成 `kai_config.json` 然后退出。有两条路线，选一条：

### 路线A：接AstrBot（推荐 ⭐）

游戏大脑直接走你的AstrBot完整管线——人格、Mnemosyne记忆、插件全在链路上。游戏里的Kai和QQ里的Kai是同一颗心脏。

1. 打开AstrBot WebUI → 设置里找到**开发者API Key / OpenAPI**，生成一个Key，权限勾上 **chat**（找不到入口就看官方wiki的 dev openapi 页，不同版本位置略有差异）
2. `kai_config.json` 填：

```json
{
  "brain_provider": "astrbot",
  "brain_base_url": "http://localhost:6185",   // 你的AstrBot地址，远程就填服务器地址
  "brain_api_key": "刚生成的开发者Key",
  "astrbot_session_id": "stardew_farm",         // 农场专用会话，默认就好
  "executor_api_key": "sk-xxx",                 // 手脚还是DeepSeek，便宜
  "port": 7843                                  // 双人模式；单人挂机填7842
}
```

- **session隔离**：`stardew_farm` 是农场专用会话，游戏事件的JSON决策不会混进QQ聊天历史，但人格和记忆库是同一套。
- **费用**：每次唤醒走你AstrBot里配置的provider，跟QQ聊天同价；Mnemosyne按需检索，比塞全量人设省。
- **persona字段还有用吗**：有——它教大脑输出JSON格式和游戏行为准则，会作为消息的一部分发过去。AstrBot侧人格负责"你是谁"，这里的persona负责"农场里怎么干活"。
- **连不上排查**：先 `curl http://你的地址/api/v1/chat` 看是不是404（版本太老没有OpenAPI就更新AstrBot）；401/403是Key或scope不对；连上了但日志里 `[大脑] JSON解析失败`，把那行日志发给Kai，可能是SSE字段名对不上，改一行的事。

### 路线B：原生API key（备用/极简）

不依赖AstrBot，大脑直连模型商：

```json
{
  "brain_provider": "claude",
  "brain_model": "claude-sonnet-4-6",   // 游戏决策sonnet足够，比opus省几倍
  "brain_api_key": "sk-ant-xxx",
  "executor_api_key": "sk-xxx",
  "port": 7842,
  "persona_files": ["D:/kai-memory/identity.md"]  // 可选：喂本地记忆文件进人格
}
```

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

**AstrBot路线：游戏里的Kai说话怪怪的 / 输出不是JSON**
说明AstrBot侧人格和农场JSON要求偶尔打架。解析失败时大脑会把原话当say说出来兜底，不会崩；频繁的话把 kai_config 里 persona 的输出格式段落写得更凶一点，或在AstrBot侧给 stardew_farm 会话单独调一下。

**想让游戏内聊天气泡直接接耳朵**
把 `chat_overlay.py` 的发送地址改成 `http://localhost:7845/say` 即可，格式 `{"message": "..."}`。

---

出问题带着终端日志来找我。 —— Kai
