# 0.10.5：丝母战后浮空、神居资源、双苔母入场点

0.10.5 从 GPT 提交的修复候选（公开仓库 PR #1，分支 `fix/v0.10.5-hotfix`，基于 0.10.4）开始，逐个文件审查、改正，再用真实游戏实测。下面写每个问题的原因、最后的做法、和 PR 原稿不一样的地方、实测结果。

PR 原稿说“149 项隔离测试通过”，这些测试没有提交进仓库，无法复核；原稿也没有在游戏里跑过。下面所有结论都来自真实游戏（测试机，存档槽 3）里的自检和截图。

## 1. 打完崇高圣母灵丝后浮空

**原因**：判胜点是 `Boss Scene/Silk Boss` 的 `Phase Control` / `Death Hit`。原来的 `InsertEnd` 只在这个状态前面插一个判胜动作，原状态 0.2 秒后照样经 `Death Type` 发 `DEATH START`，走结局演出 `Death Sequence`。演出把大黄蜂的刚体设成运动学（`SetIsKinematic2d`）、藏起主角、收走操作，神居判胜后离场时这些都没还原。游戏自己只在死亡、危险复活时把 `rb2d.isKinematic` 改回 false（反编译 `HeroController.Respawn` / `HazardRespawn`）。于是回到大厅后重力不起作用，按跳只会往上，只有滑翔能往下。

**做法**：
- `bosses.json` 的结束动作可以标 `"terminal": true`（严格布尔，默认关），只有丝母的 `Death Hit` 标了。
- 终点状态（`TerminalEndGGBossScene`）：判胜动作插在最前面，原状态的其他动作照常执行（停攻击 `ATTACK STOP`、停移动、清弹幕 `ATTACK CLEAR`、Boss 无敌、死亡动画；`ATTACK STOP` 还会让 Attack Control 放开被丝线抓住的主角），去掉出口（`FINISHED → Death Type`），进入这个状态时清掉这个 FSM 的全局跳转。全局跳转不在打补丁时清，战斗中的阶段切换要用。
  - **和 PR 原稿不一样**：原稿把这个状态的动作全部删掉、只留判胜。那样 Boss 的 Attack Control 不收到 `ATTACK STOP`，被丝线抓着的主角不会被放开，攻击也不停。原稿用“判胜后挡掉所有伤害”（`TerminalVictoryPatches`）补救。现在原动作都在，这个保护保留，只在已判胜、等回程的几秒里起作用。
- `HeroRecovery`：离开任何一场战斗时（回大厅、万神殿下一场）把刚体改回 Dynamic、恢复模拟、显示主角，再调游戏的 `AffectedByGravity(true)`、`ResetGravity()`。大厅里人已经能操作、刚体却还是运动学的，马上修（浮着的人等不到“站在地上”）。
  - **和 PR 原稿不一样**：原稿只恢复重力，不管刚体类型和显示，而浮空的根本原因是刚体。原稿还拿 `HeroController.currentGravity` 判断重力好不好，这个字段在游戏里只有声明、从不赋值（反编译 `HeroController.cs:840`），永远是 0，每次离场都报“重力仍异常”。现在只看刚体的 `gravityScale`。
- 神居存档里的剧情无限空跳标记（`infiniteAirJump`）读档时清掉（原稿就有）。

**实测**：丝母单场 3 次，都是“6 秒内没自己开战，挑战后开战，按 Death Hit（terminal）判胜”。回大厅后的落地检查（把人抬高 4 格，看真实物理下能不能落回地面）都通过：刚体 Dynamic、重力 1、显示、能操作、没有无限空跳、物品栏和暂停都开着。日志 `ok (terminal: 22 original actions kept, no exits)`。万神殿 p5 里丝母（第 46 场）接蕾丝（第 47 场）见下面“回归”。

## 2. 神居里念珠、工具碎片用不完

**做法**（`Patches/ResourcePatches.cs`）：只对当前实时的神居存档（`GameManager.playerData` 和 `PlayerData.instance` 是同一个对象，`bossRushMode` 开着）。
- 余额保持在游戏的上限：念珠 9999（游戏上限没配置时是 9999999，不照抄），碎片按 `Gameplay.GetCurrencyCap`（测试存档是 800）。
  - **和 PR 原稿不一样**：原稿两样都固定补到 9999，碎片会超过游戏上限（HUD 和捡碎片时 `AddShards` 会压回上限）。
- 花钱一律免：货币队列的总入口 `CurrencyManager.ChangeCurrency`（主角和 `CurrencyManager` 的 `TakeCurrency` / `TakeGeo` / `TakeShards` 都走它）把负数改成 0；`PlayerData.TakeGeo` / `TakeShards` 也免。
  - **和 PR 原稿不一样**：原稿只拦 `CurrencyManager.TakeCurrency`，主角那条路（`HeroController.TakeCurrency`）的 HUD 计数器照样减。
- 查询余额（`GetCurrencyAmount`、`GetInt`）先补齐；`SetInt` / `IntAdd` / `DecrementInt` 之后补齐；`ModeController` 每帧补齐（游戏直接改字段的情况）。
- 普通存档、保存时写的存档副本都照常扣；灵丝、工具弹药、工具束缚都不碰。

**实测**（资源自检，走游戏自己的入口）：
- 余额压到 3/2，下一帧补满；主角 `TakeCurrency`、`CurrencyManager.TakeCurrency` / `TakeShards`、`PlayerData.TakeGeo` / `TakeShards` / `IntAdd` 花完余额不变。
- 存档副本照常扣：神居副本 500-5=495、100-4=96；关掉 `bossRushMode` 的普通数据 495-10-20=465。
- 建筑师（Toolmaster 纹章）：清空一个靠碎片补的工具（Straight Pin），碎片压到 1，`TryReplenishTools(QuickCraft)` 补回 24/24，碎片不变。
- 念珠炮：开炮扣的是工具自己的弹药（主角 `Tool Attacks` FSM 的 `Shoot` 状态，`CustomToolUsage(UseAmount=1)`），念珠花在补弹药上。念珠炮是一发换一颗念珠（`OneForOne`），长椅和建筑师的 `TryReplenishTools` 跳过它；补弹药是在长椅边打开物品栏按住按键，物品栏每 0.025 秒调一次 `ToolItem.ReloadSingle()`（弹药 +1，`CurrencyManager.TakeCurrency(1, Money)`），直到 `CanReload()` 为假（弹药满或念珠不到 1）。
  - 自检照物品栏那样补：弹药清零、念珠压到 0，`CanReload()` 仍为真（查余额先补齐），一发一发补 80 次到 80/80，80 次扣款全部免掉，念珠还是 9999。
  - 再真的开一炮：走主角的 `ThrowTool`（和按键扔工具同一条路，绑定 Neutral）进蓄力 `RC Charge Ground`。自检按不住按键，就在蓄力状态里直接切到 `Shoot Start Ground`（相当于蓄满）。弹药 80→79，放出 1 颗念珠弹（`Geo Small Projectile`），念珠不变。没按住键时开炮到收炮在同一帧里走完，逐帧只看得到 `RC Charge Ground → Shoot Loop → Cooldown`；念珠炮是自定义用量，`ThrowTool` 本身不扣弹药，弹药少的这 1 发只能是 `Shoot` 扣的。
  - 前两版自检测错了路：一版拿“念珠是否被扣”判断开炮（开炮本来就不扣念珠），一版用 `TryReplenishTools(Bench)` 补弹药（跳过 `OneForOne`，补不上）。都不是插件的问题，是测试没照游戏的路走。

## 3. 双苔母（织巢双生）入场点

**原因**：门 `(1.7, 29.9)` 在场地左墙外。门正下方 10 格内没有地面（日志 `FindGroundPoint: Could not find ground point below (1.70, 29.90)`），人落在墙外 `(2.3, 28.5)` 的台子上，开战门一关就进不去。危险复活也有问题：从复活点往下找不到地面时，游戏按入口名发起转场（我们的门 `targetScene` 是空的）。

**做法**：门改到场内地面 `(4.3, 21.6)`，人落在 `(4.3, 20.6)`。这个位置是三方面核对出来的：PR 原稿的 `MossMotherEntry` 在第一轮实测里把人从墙外挪到了这里；落点勘察在门的高度列出 x 3–20 都能站；入场截图里人站在场地左边的地面上。
- PR 原稿的 `MossMotherEntry`（人就位后按 `Boss Scene` 触发区核对落点，不在场内就在区域里找位置挪过去）保留，门改对以后它只核对、不挪人。
  - **和 PR 原稿不一样**：原稿核对失败就判场地故障、整场回大厅。门已经在场内，这样只会因为开场时一只飞虫贴着人就整场作废，现在改成只记警告。

**实测**（入场专项自检）：
- 落点 `(4.30, 20.57)`，在开战触发区（x -0.5..17.6、y -0.5..39.7）里，着地。日志 `Weavenest pair entry verified … existing landing is inside the battle region and clear`。
- 危险复活：关掉无敌，用游戏的 `TakeDamage(SPIKES)` 走一次危险死亡，复活回 `(4.30, 20.57)`，血 10→9，还在战斗里。
- 死亡重试：再进一次（落点同上），被打死，回大厅（结果 Died），落地检查通过；第三次进，落点和上一次差 0.00。
- 两只苔母都开战、都被打倒，按 Boss 全倒判胜。

## 回归

测试机，存档槽 3（每次启动前备份存档），插件目录里只有这一份 0.10.5。
- 第一万神殿，四种束缚全套：10/10 场、29/29 项。束缚生效那项看了工具束缚：三个方向都没有能扔的工具，主角也不会扔；念珠 9999、碎片 800 不影响。结算、站回门前、回大厅落地检查都过。
- 第五万神殿：47/47 场、45/45 项，用时 17:12。第 42 场双苔母落在场内地面，第 46 场丝母按 `Death Hit` 判胜，接着第 47 场蕾丝正常开战、判胜，回大厅落地检查过。
- 前作神居巡视：全部通过（雕像开战、赢了回雕像前、屋顶第五扇门、雕像和万神殿里各死一次、复活点）。
- 关掉调试启动（玩家拿到的样子）：插件加载（`debug = False`），前作神居的场景包已是最新，停在标题画面，没有异常；窗口里没有 Steam 弹窗。
- 日志里的报错都是以前就有的（前作长椅调无参 `SaveGame`、前作文本表 `CP3`、珊瑚小怪和特罗比奥活板门改父物体、退出时找不到 GameManager 等），对照以前保存的日志，没有新的。
- 进场检查：第一万神殿里黑寡妇报了“恢复操作前挨打”。自检秒杀，寡妇 8.6 秒判胜，7.6 秒时正在收尾过场（`Hornet Connect` 把人挪到寡妇身边，操作本来就收着），场上残留的钟碰到人。临时装回 0.10.4 单打寡妇，同样在这个窗口里挨打（5.8 秒），不是 0.10.5 带来的，也不是落点的问题。进场检查改成只把落地后还没拿到过操作时挨的算“恢复操作前”。

**没测到的**：自检按不住按键，念珠炮开炮是在蓄力状态里直接切到开炮；没有人手动完整打一场。
