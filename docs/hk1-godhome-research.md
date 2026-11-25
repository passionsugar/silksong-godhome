# 前作神居是怎么做的、本作还剩什么、我们能借什么（2026-11-23）

> 2026-11-24 更新：安全规则 5、6 已取消。以前作为依赖、运行时加载前作神居场景的调研和试验见 [hk1-port.md](hk1-port.md)。下文是规则取消前写的。

你问：前作的神居地图是从头新建的，还是原地图加东西、或者开了特殊代码？这些代码本作有没有预制、有没有藏着、能不能用？能的话手办屋（忆像长廊）和万神殿都可以按前作的方式重构。

下面每条都标了出处。前作只查了数据结构和代码，没有导出、没有使用任何前作素材（安全规则 5）。分析脚本和原始结果在 `reference/hk1/analysis/`（`tools/hk1_gg.py`、`tools/ss_gg_search.py`），不进 git（安全规则 6）。

## 一句话结论

- **前作**：每个 Boss 都有一个**专门新建的神居场景**（共 77 个 `GG_` 场景），里面是从原版 Boss 场景拷过来的 Boss 本体和战斗物体，加上两个**共用预制体**和重新布置的神居风格布景；**Boss 自己的 FSM 原版里就写好了“神居分支”**。三者都是开发者在编辑器里一个个做的。
- **本作**：只留下了前作的**代码类**（`BossSceneController`、`BossSequenceController`、雕像、挑战界面、一整套 `GG*` FSM 动作），**内容一样都没有**：没有神居场景、没有那两个预制体、没有万神殿数据，本作 Boss 的 FSM 里也没有神居分支。
- **所以没有“藏着的神居”可以直接打开**。能借的是：几段还在生效的引擎代码，以及前作的**结构**（每个 Boss 一份明确写好的场地、统一的入场门和回程门、一个场地控制器）。

## 1. 前作怎么做的（证据）

### 1.1 场景清单

- 前作 build 里 501 个场景，`GG_` 开头的 77 个，单独放在 `Assets/Scenes/Gods_Glory/`（25 个：前厅、长廊 `GG_Workshop`、各层、结局）和 `Assets/Scenes/Gods_Glory/Bosses/`（52 个 Boss 场景，含 `_V` 变体）。
- 出处：`hollow_knight_Data/globalgamemanagers` 的场景清单，结果见 `reference/hk1/analysis/gg-scenes.md` 第 1 节。

### 1.2 神居场景和原版 Boss 场景对比（10 对）

前作原版的 Boss 战多半在一个很小的附加场景里（例如 `Fungus1_04_boss` 只有 46 个物体），由主房间的 `BossLoader` 叠加加载。拿神居场景和它对比：

| 神居场景 | 物体数 | 原版 Boss 场景 | 物体数 | 物体名重合 |
|---|---|---|---|---|
| GG_Hornet_1 | 539 | Fungus1_04_boss | 46 | 31 |
| GG_False_Knight | 532 | Crossroads_10_boss | 56 | 29 |
| GG_Mantis_Lords | 571 | Fungus2_15_boss | 74 | 22 |
| GG_Soul_Master | 584 | Ruins1_24_boss | 719 | 35 |
| GG_Traitor_Lord | 699 | Fungus3_23_boss | 109 | 35 |
| GG_Dung_Defender | 590 | Waterways_05_boss | 19 | 16 |
| GG_Mega_Moss_Charger | 977 | Fungus1_29 | 1485 | 144 |
| GG_Nosk | 628 | Deepnest_32 | 1212 | 81 |
| GG_Broken_Vessel | 575 | Abyss_19 | 2791 | 92 |
| GG_Hollow_Knight | 584 | Room_Final_Boss_Core | 655 | 95 |

看得出来：

1. **Boss 本体和战斗物体是从原版拷过来的**（`Battle Scene/False Knight New`、`Mantis Battle/Battle Main/Mantis Lord`、`Battle Scene/Wave 3/Mantis Traitor Lord` 路径都一样）。
2. **其余几百个物体是新搭的**：瓦片地图、重新布置的布景（`gg_pillar_corner_large`、`GG_scenery_…_statue_clouds`、`gg_mantis_spikes`、`mantis_cage_gg`、`gg_particles_standard`），和原版房间几乎不重合。
3. **每个神居场景都有同样两个共用预制体**：
   - `Boss Scene Controller`：挂 `BossSceneController`，两个 FSM：`Dream Return`（淡出、回血、回去）和 `Dream Enter Next Scene`（万神殿去下一场，`BeginSceneTransition`）。子物体 `Dream Entry`（FSM `Control`：Pause → Take Control → First Boss? …，进场时接管操控）和入场门 `door_dreamEnter`（门型 `TransitionPoint`）。
   - `GG_Arena_Prefab`：`Floor`、`Wall_L`、`Wall_R`、`Roof`、`BG`、`Crowd`、`Godseeker Crowd`（观众）、`Darkness Region`。
4. **原版房间里的门、长椅、NPC、宝箱、拾取物在神居场景里都没有**（例如原版的 `Ghost False Knight NPC`、`Chest`、`Geo Rock`、`Breakable Wall` 都不在）。神居场景里唯一的门就是 `door_dreamEnter`。

出处：`gg-scenes.md` 第 2 节。

### 1.3 Boss 的 FSM：原版里就写好了神居分支

- 8 对里 Boss 本体身上的 PlayMakerFSM，神居版和原版的字符串（状态名、事件名、动作名、变量名）**完全一样**：大黄蜂、假骑士、苔藓冲锋者、螳螂领主、诺斯克、破碎容器、叛徒领主。
- 只有两处差别，都是神居版**少了**东西：
  - 灵魂大师少了“打败过就销毁”（`Destroy If Defeated` / `mageLordDefeated`）。
  - 粪便守卫少了“按骨钉等级调血量”（`nailSmithUpgrades`、`hp_scaler`）。
- “是不是神居场景”的判断动作 `GGCheckIfBossScene` 出现在 71 个场景：38 个神居场景，加上 **33 个原版场景**（`Fungus1_04_boss`、`Crossroads_10_boss`、`Fungus2_15_boss`、`Ruins1_24_boss`、`Deepnest_East_Hornet_boss` 等）。也就是说，**Boss 预制体本身就带着“在神居里怎么做”的分支**（跳过开场、改行为），同一份 FSM 在原版和神居里都用。
- 其他神居动作只在神居场景里：`GGWaitForBossSceneTransitionIn`、`GGCheckIfFirstBossScene` 各 63 个（共用预制体里的），`CheckGGBossLevel` 51 个（按难度改行为），`EndGGBossScene` 14 个（特殊结局的 Boss 自己宣布结束）。
- 代码里也一样：前作的 `HealthManager` 有按难度改血量的 `HPScaleGG`，`GeoControl` 和 `EnemyDeathEffects` 在神居场景里不掉钱、不出灵魂。

出处：`gg-scenes.md` 第 2、3 节；前作反编译 `HealthManager.cs`、`GeoControl.cs:105`、`EnemyDeathEffects.cs:199`。

### 1.4 进出流程（前作代码）

- 挑战：`BossChallengeUI.LoadBoss` 选场景（按难度可以是不同场景 `Tier1Scene`…），设 `BossSceneController.SetupEvent`（难度、`DreamReturnEvent`、打赢记完成），把回程门名写进 `playerData.bossReturnEntryGate`，再以 `door_dreamEnter` 为入口切场景。
- 进场：游戏按入口名找到神居场景里的 `door_dreamEnter`，把骑士放在门口（门型入口）。`Dream Entry` 接管操控、等转场。
- 结束：`BossSceneController` 登记的 Boss 都死了 → 等几秒 → `Dream Return` 淡出 → 以 `door_dreamReturn` 为入口回去，`GameManager.EnterHero` 把它换成 `bossReturnEntryGate`，骑士出现在雕像 / 门前。
- 万神殿：`BossSequenceController` 在每场的 `BossSceneController` 上挂回调，打完由 `Dream Enter Next Scene` 去下一场的 `door_dreamEnter`；序列数据从 `Resources/GG/<名字>` 读。

## 2. 本作还剩什么

### 2.1 代码：类都在，没人用

`BossSceneController`、`BossSequenceController`、`BossScene`、`BossSequence`、`BossStatue`、`BossStatueLever`、`BossChallengeUI`、`BossDoorChallengeUI`、`BossDoorChallengeCompleteUI`、`BossSequenceDoor`、`BossSummaryBoard`，以及 `GGCheckIfBossScene`、`CheckGGBossLevel`、`EndGGBossScene`、`GGWaitForBossSceneTransitionIn` 等 FSM 动作，都在本作的 Assembly-CSharp 里。

- 本作 56 个 Boss 场景的 dump 里，**没有一个物体挂 `BossSceneController`，没有一个 FSM 用到任何 `GG*` 动作**（`%TEMP%\gh_all` 全文搜索 0 处）。
- 本作的数据文件里**搜不到**：`door_dreamEnter`、`Dream Enter Next Scene`、`Boss Scene Controller`、`GG_Arena`、`Godseeker`、`GG_Workshop`、`GG_Atrium`、`GG_End_Sequence`、`bossSceneToLoad`、`Dream Return`。这些类名只在 `globalgamemanagers.assets` 里作为脚本登记出现（`silksong-gg-leftovers.md`）。2068 个资源包的文件名里没有一个和神居有关。
- 本作的 `HealthManager` 没有前作的 `HPScaleGG`；`GeoControl`、`EnemyDeathEffects` 也不再看“是不是神居场景”（本作改看忆境）。

### 2.2 还在生效、我们能用的几段

| 代码 | 在本作里的效果 | 我们现在用没用 |
|---|---|---|
| `BossSceneController.IsBossScene` | `GameManager.CanPickupsExist` 返回 false（场景里不出拾取物）；`HeroController` 按 `BossLevel` 改受到的伤害（1 级 ×2，2 级一击死） | 用了：0.9.0 起每场都挂（`Mode/Arena.cs`），也用它的 `bosses` / `OnBossesDead` 判胜 |
| `BossSequenceController.BoundShell` / `BoundMaxHealth` | `PlayerData.CurrentMaxHealth` 封顶（面具束缚） | 用了（补丁 getter） |
| `BossSequenceController.BoundNail` / `BoundNailDamage` | `GetNailDamage` 动作取封顶后的伤害 | 用了（补丁 getter） |
| `GameManager.ReturnToMainMenu` | 在连战中回标题时解开束缚 | 自动生效 |
| `door_dreamReturn` + `playerData.bossReturnEntryGate` | `GameManager.EnterHero`（GameManager.cs:3313）把入口换成存档里记的门名。本作自己的忆境回程也在用这个门名（`WorldNavigation` 里十几处） | 用了（0.9.0）：圣所里每尊雕像、每扇万神殿的门前各建一个回程门 |
| 运行时新建 `TransitionPoint` | `Awake` 里自己登记（TransitionPoint.cs:153）；名字带 `door` 就当门型入口（:452），`EnterHero` 按名字找（GameManager.cs:3292、3365） | 用了（0.9.0）：场地加载时建 `door_dreamEnter` 当入口 |
| 主菜单隐藏的 `GGButton`、`bossRushMode` | 新建神居存档 | 用了（第二版·1） |

### 2.3 用不了的

- `BossSequenceController` 的连战流程：要靠每个场景里 `Boss Scene Controller` 预制体上的 `Dream Enter Next Scene` / `Dream Return` FSM，和 `Resources/GG` 下的序列数据，本作都没有。我们已经自己实现了同样的东西（快照、带血、休息点、计时、无伤）。
- 雕像、挑战面板、结算界面的类：都要配套的界面预制体，本作没有。我们已经照它们的逻辑用本作的素材重做了。
- 神居场景本身：本作没有，也没法在游戏里“打开”。

## 3. 对我们的启发

前作能做到“进去就打、每个 Boss 都稳”，靠的不是一段通用代码，而是**每个 Boss 都有人手工做过一遍**：拷 Boss、搭场地、放入场门，必要时在 Boss 的 FSM 里加神居分支。本作没有这些内容，我们的 `bosses.json` 的 `dream` 段其实就是在补这份“每个 Boss 一份”的工作。

今天连着出的三个问题（幽影、监工兄弟、伏特维姆）都出在**运行时自动猜开战触发区**：场景里有音效、雾这类同样等玩家进入的触发区，猜错了。前作根本不猜：每个场景的入场门和开战方式都是明确做好的。

可以照前作改的三处：

1. **每个 Boss 一份明确的场地数据**（对应前作每个 `GG_` 场景）：入场位置、开战触发区、要关掉的东西、结束条件都写清楚。自动找触发区只留在**离线脚本**里，用来从场景 dump 生成草稿，我再逐个核对；游戏运行时不再猜。没写的 Boss 才退回自动，并在日志里警告。
2. **真正的入场门和回程门**（对应 `door_dreamEnter` / `door_dreamReturn`）：
   - 进场时在场地里建一个门型 `TransitionPoint`（名字带 `door`），用它当入口切场景，让游戏自己把大黄蜂放在门口，不再“从原场景的门进来再传送”。
   - 回来时用游戏自己的 `door_dreamReturn` + `bossReturnEntryGate`，回到雕像 / 万神殿的门前。
3. **一个场地控制器**（对应 `Boss Scene Controller` 预制体）：每场都挂 `BossSceneController`（不只重织 / 断丝），统一管入场、清场、束缚、结束、回程；现在这些分散在 `ModeController` 和 `DreamArena` 里。

手办屋和万神殿的界面（雕像、拉杆、挑战面板、结算）已经照前作的逻辑做好了，前作没有更多可借的；要改的主要是**它们背后的进出流程**（上面第 2 条），这样两边共用一套。

> **2026-11-23 实现（0.9.0）**：按方案 A 重构，旧的 `DreamArena` 整个删掉，做法见 [`arena.md`](arena.md)。和上面第 1 条不同的一点（你定的）：运行时完全不猜，没写场地数据的 Boss 不能开战；自动找只在离线工具里出草稿。

## 4. 方案

| | A：照前作的结构重构（推荐） | B：只修自动挑选 | C：照前作新建独立场景 |
|---|---|---|---|
| 做什么 | 上面三处：每个 Boss 明确的场地数据（离线生成草稿 + 逐个核对）、真正的入场门 / 回程门、统一的场地控制器 | 同样近的候选里，优先 Boss 自己 / 战斗本身的触发区；其他不变 | 装 Unity 6 编辑器，导出本作的 Boss 场景，在编辑器里裁出场地、重搭、打包成资源包，运行时加载 |
| 好处 | 不再靠猜；进场是游戏自己的入场流程（镜头、站位都对）；手办屋和万神殿共用一套进出 | 改动最小 | 和前作做法完全一样 |
| 代价 | 50 个 Boss 的数据要逐个核对一遍；进出流程是大改，要全量回归（50 个单场 + 五个万神殿） | 仍然是猜，三殿到五殿还可能碰到新的情况 | 工作量最大；要导出大量本作资源；游戏一更新就容易坏；Boss 的 FSM 绑着原场景的物体，裁场景很容易弄坏 |
| 风险 | 运行时建的入场门要实机验证（游戏找门的时机） | 低 | 高 |

我推荐 **A**：前作稳定靠的就是“每个 Boss 一份明确的东西”，A 是在本作里能做到的最接近的版本。三殿到五殿本来就要全量测，A 的回归正好和它一起做。

## 5. 不新建地图、只“复制原场地再轻改”，其余全照前作：做得到吗（你追问的）

做得到。关键是改的时机：

- 本作每次进 Boss 战，游戏都会从资源包里重新加载一份原场景。这一份就是“复制品”：我们改它不会动游戏文件，下次加载又是干净的一份（安全规则 2）。
- 加载时有一个时刻：场景里的物体都建好了（Awake 跑完），但都还没开始运行（Start 没跑、FSM 没开始），大黄蜂还没放进来，画面还是黑的。游戏随后才找入口门、放人、淡入（GameManager.cs:3081-3131：加载 → `OnNextLevelReady` → `EnterHero`）。圣所就是在这个时刻搭起来的（`Mode/Sanctum.cs`，`SceneManager.sceneLoaded`）。
- 在这个时刻把场景改成“神居版”，玩家从头到尾看到的都是改好的场地。效果上就等于前作在编辑器里复制一份再改。

前作的每个做法，对应到原场地上：

| 前作 | 在原场地上怎么做 | 能不能做到 |
|---|---|---|
| 独立的 `GG_` 场景 | 每次加载的原场景就是一份新副本，加载时改 | 能。唯一不同：场景名还是原名（地图、区域标题、场景存档都按原名），用快照还原和屏蔽处理 |
| 去掉门、长椅、NPC、宝箱、拾取物 | 加载时关掉（现在进场后才关，提前到加载时） | 能 |
| `GG_Arena_Prefab`：地面、墙、屋顶 | 加载时按原场景的战斗镜头锁范围加空气墙；观众这类装饰可以用本作素材少量加 | 能（装饰可选） |
| `Boss Scene Controller` 预制体 | 加载时放一个控制器物体，挂游戏自己的 `BossSceneController` | 能 |
| 入场门 `door_dreamEnter` | 加载时建一个门型 `TransitionPoint`，以它为入口切场景，游戏自己把人放在门口 | 能，要实机验证 |
| `Dream Entry`（接管操控、等转场、放开） | 照前作 FSM 的步骤自己写 | 能 |
| Boss FSM 里的神居分支（`GGCheckIfBossScene` 跳开场等） | 本作 Boss 没有，在 FSM 还没开始跑时按表给它加 / 改跳转（例如幽影总控一进场就开战），每个 Boss 一份 | 能，要实机验证；这是工作量的主体，前作也是每个 Boss 单独做的 |
| 特殊 Boss 自己宣布结束（`EndGGBossScene`） | 本作还留着这个 FSM 动作，可以插进这个 Boss 的死亡状态 | 能，要实机验证 |
| 普通 Boss 死光就结束（`BossSceneController.bosses`） | 把 Boss 的 `HealthManager` 登记进去，本作 `HealthManager.OnDeath` 还在（HealthManager.cs:582） | 能 |
| 按难度改血量（`HPScaleGG`） | 本作没有这段，补丁 `HealthManager` | 能（可选） |
| 不掉钱、不出拾取物 | `IsBossScene` + 忆境规则 | 已经做到 |
| 回程 `door_dreamReturn` → 回雕像 / 门前 | 游戏自己的 `bossReturnEntryGate` | 能 |
| 万神殿去下一场 | 下一场同样以 `door_dreamEnter` 进 | 能（连战逻辑已有） |

和前作不一样的只有两点：

1. **场地外观是原版的**（没有前作重新布景的神居风格），这是你说可以接受的。
2. **场景名还是原名**。也可以真的在内存里另建一个新名字的场景，把物体搬过去，但游戏很多东西按场景名存取（场景状态、地图、场景管理器、资源卸载），搬过去容易坏，而且没有必要：每次加载本来就是一份新副本。
