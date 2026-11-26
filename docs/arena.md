# 神居场地（第三版）：加载时把原场地改成神居版

2026-11-23 开始。按 `hk1-godhome-research.md` 第 5 节定下的做法重构：**不新建地图**，每次加载的原 Boss 场景就是一份新副本；在它“物体建好、还没开始跑、人还没放进来、画面全黑”的那一刻，用前作神居的技术手段把它改成神居版。

旧做法（第二版·2 的“梦境场地” `DreamArena`）整套删掉，不留兼容分支：

| 删掉的旧做法 | 换成 |
|---|---|
| 从原场景的门（`entry.gate`）进来，再把人“放进”自动找到的开战触发区（`FindWakes` / `MoveInto` / 开门触发区 / 睡着的触发区 / 盯 10 秒追着挪） | 运行时建的入场门 `door_dreamEnter`，以它为入口切场景，游戏自己把人放在门口（门的位置写在表里） |
| 进场后清场（`ClearInteractables`）、进场后改场景（`setup` 两遍、`dream.start`） | 加载时清场、加载时按表改 FSM（相当于前作 Boss FSM 里的神居分支） |
| 判胜靠击败标记 / 图鉴 / 血量归零后等宽限期 | 游戏自己的 `BossSceneController`：Boss 的 `HealthManager.OnDeath` 都到了（`bosses`），或者特殊 Boss 在表里指定的状态里插 `EndGGBossScene` |
| 重织 / 断丝：单独挂一个不激活的 `BossSceneController` | 场地控制器上的 `BossSceneController.BossLevel` |
| 回大厅从长廊入口 `right1` 进来，黑幕下再把人挪到雕像 / 门前（`PlaceAtStatue`） | `door_dreamReturn` + `playerData.bossReturnEntryGate`，游戏自己把人放在雕像 / 门前的回程门 |
| 运行时自动猜（找触发区、找落点、按距离挑） | 没配置的 Boss 不能开战。通用场地分析只在离线脚本里生成草稿；双苔母另有仅检查已配置 BattleScene 的一次入场安全校验（见 1.3） |

## 1. 流程

### 1.1 开战（单场、万神殿每一场都一样）

1. 拍快照、按表改 PlayerData（`flags.defeated` 设 false、`flags.encountered` 设 true、`prerequisites`）、清持久记录（不变）。
2. 以 `entry.scene` + **`door_dreamEnter`** 为入口切场景（`GameManager.BeginSceneTransition`）。
3. `SceneManager.sceneLoaded`（物体 Awake 已跑、Start 没跑，人没放进来）：对这场战斗的每个场景（入口场景、宿主场景、附加的 Boss 场景）调 `Arena.Prepare`：
   - **入口场景**：建入场门 `door_dreamEnter`（见 1.3）。
   - **每个场景**：关出口（见 1.4）、关交互物体（见 1.5）、跳过 Boss 前的小怪波次（见 1.8）、按表打补丁（见第 2 节）。
   - **Boss 所在的场景**（`scene`；附加场景 Boss 就是附加场景）：建场地控制器（见 1.2）。
4. 游戏自己找入口：`GameManager.EnterHero` 按名字找到 `door_dreamEnter`（GameManager.cs:3292、3365；旧场景在这之前已经卸载，GameManager.cs:786），`HeroController.EnterScene` 走门型入口：`FindGroundPoint(门的位置)` 站到门下的地面上，淡入，`FinishedEnteringScene` 把危险复活点设成门的 `HazardRespawnMarker`（HeroController.cs:6867-6905、9284）。
5. `FinishedEnteringScene` 后缀：确认是从 `door_dreamEnter` 进来的（`HeroController.GetEntryGateName`），开始计时。场地没准备好（没建门 / 没建控制器）就报错回大厅，不在没改过的原场景里打。
6. 人就位（`isHeroInPosition`）再过一个物理帧：给人所在的开战触发区补发“进入”，再执行表里的进场步骤（`arena.enter`，相当于前作 `Dream Entry` 的最后几步）。入场门在开战触发区里时，加载那一刻 Unity 发的“进入”有两种收不到的情况，人不出去就不会再有下一次，Boss 一直不开打：
   - `BattleScene`：`OnTriggerEnter2D` 只认 `isHeroInPosition` 为真的人（BattleScene.cs:214-223），加载那一刻的“进入”被它忽略（0.9.0 冒烟测试：苔骨母 Tut_03，`Re-Encounter` 打开触发区时人还没就位）。补法：用人的碰撞体查重叠的触发区（`Collider2D.Overlap`，只要启用的触发区），属于本场战斗的场景、挂着 `BattleScene` 的，直接调它的 `OnTriggerEnter2D(人的碰撞体)`，补一次。
   - PlayMaker：`Trigger2dEvent` 只在“等进入”的那个状态里才挂上监听（`OnEnter` 里 `AddCallback`，Trigger2dEvent.cs），而 FSM 常常要等人就位（`WaitForHeroInPosition`）才走到这个状态，加载时的“进入”早就过去了（0.9.0 全量自检：第四圣咏团，`Boss Scene` 的 `Control` 停在 `Remeet Ready`，人就站在 `Start Range` 里）。补法：人就位后 3 秒内每个物理帧看一次，本场地哪个 FSM 的当前状态里有启用的“进入”监听（`Trigger2dEvent` / `Trigger2dEventLayer`，类型 `OnTriggerEnter2D`），而人已经在它看的那个物体的触发区里（触发区所在物体或它挂的刚体所在物体），就把“进入”交给这个动作，每个动作一次。它在这个状态里还没收到过“进入”，收到过早就跳走了，所以不会重复。
   - 两种都等于玩家刚走进来，开不开战仍由 `BattleScene` / FSM 自己判断（打过了、已经开打、触发区没开、标签不对都不会开）。日志写 `补发进入 / re-sent enter`。

### 1.2 场地控制器（对应前作 `Boss Scene Controller` 预制体）

在 Boss 所在场景里建 `Godhome Boss Scene Controller`，挂游戏自己的 `BossSceneController`（BossSceneController.cs）：

- 先建成不激活的物体，填好公开字段：`doTransitionIn = false`、`doTransitionOut = false`（本作没有前作的转场预制体和结束事件，不关会报错）、`bossesDeadWaitTime = 1.5`（倒下后的剧情来不及开始就离开）、`bosses` = 表里 Boss 物体上的 `HealthManager`（特殊结局的 Boss 留空）。
- 设 `BossSceneController.SetupEvent`（照前作 `BossChallengeUI.LoadBoss`）：`BossLevel` = 难度；私有的 `doTransition`、`restoreBindingsOnDestroy` 设 false（不然 `Start` 报没有转场预制体、`OnDestroy` 里 `RestoreBindings` 会回满血）；订 `OnBossesDead` → 判胜、`OnBossSceneComplete` → 回程 / 下一场。
- 再激活：`Awake` 里 `Instance = this`，调 `SetupEvent`，`Setup()` 给每个 `bosses[i].OnDeath` 计数（BossSceneController.cs:107-195）。

效果（都是游戏自己的代码）：

- `IsBossScene` → `GameManager.CanPickupsExist` 返回 false：不出拾取物、不掉念珠和贝壳碎片（GameManager.cs:5487，`CurrencyObjectBase`、`CollectableItemPickup` 都看它）。
- `BossLevel` 1 受到的伤害翻倍、2 一击死（HeroController.cs:5321）。
- Boss 都死了 → `EndBossScene` → `OnBossesDead`（立刻判胜）→ 等 `bossesDeadWaitTime` → `OnBossSceneComplete`（回程或下一场）。人死了、正在转场时不触发（BossSceneController.cs:263-274），由死亡流程和转场过滤接管。
- 本作很多 Boss 是“特殊死亡”（`HealthManager.hasSpecialDeath`：血量归零只发 `ZERO HP` 给 FSM，不调 `OnDeath`，HealthManager.cs:1645-1661）。这种 Boss 照前作的做法，在它 FSM 的死亡状态最前面插一个游戏自己的 `EndGGBossScene` 动作（EndGGBossScene.cs：`BossSceneController.Instance.EndBossScene()`），表里写在 `arena.end`。

### 1.3 入场门 `door_dreamEnter`

运行时新建，挂游戏自己的 `TransitionPoint`（先建不激活的物体、填字段、再激活，`Awake` 里自己登记到 `TransitionPoint.TransitionPoints`，TransitionPoint.cs:148-159）：

- 名字 `door_dreamEnter`：名字带 `door` 就是门型入口（TransitionPoint.cs:452）。
- `isADoor = true`、`isInactive = true`：不能交互（`EnableInteractableFields` 返回 false，`Awake` 里 `Deactivate`）；没有碰撞体，不会被走进去触发转场。
- `dontWalkOutOfDoor = true`：站在门口，不播“从门里走出来”。`alwaysEnterRight` / `alwaysEnterLeft` 按表里的朝向。
- 子物体 `HazardRespawnMarker`：危险复活点（掉进尖刺、酸池后回到门口）。
- 位置 = 表里的 `arena.door`。门型入口只用 x 和“往下找地面”（`FindGroundPoint`），所以 y 写在地面上方一点就行；不能写在墙里。

#### 双苔母的入场点（0.10.5）

`moss_mother_duo` / `Weave_03` 原来的门 `(1.7, 29.9)` 在场地左墙外：门正下方 10 格内没有地面（`FindGroundPoint` 报错），人落在墙外 `(2.3, 28.5)` 的台子上，开战门一关就进不去（玩家报告“卡在地图外面”）。危险复活也会出问题：`HeroController.HazardRespawn` 从复活点往下找不到地面时，按入口名发起去 `targetScene`（我们的门是空的）的转场。

0.10.5 把门改到场内地面 `(4.3, 21.6)`（落在 `(4.3, 20.6)`，开战触发区 `Boss Scene` 是 x -0.5..17.6、y -0.5..39.7）。实机自检（DebugTools.Entry.cs）核对：落点在触发区里、着地；用游戏的 `TakeDamage(SPIKES)` 走一次危险死亡，复活回同一点；被打死回大厅、再进一次，落点不变。

保留 GPT 修复候选里的 `MossMotherEntry`：人就位后、补发开战触发前，按 `Boss Scene` 触发区核对整个身体、障碍和脚下地面，不在场内才在区域内找一个位置挪过去并同步危险复活点。门改对以后它只报“existing landing ... clear”，不挪人；校验没通过（比如开场时有飞虫贴着人）只记警告、留在门前，不再判场地故障回大厅。其他 Boss 不启用。

### 1.4 出口（对应前作 `GG_Arena_Prefab` 的墙）

这场战斗的场景里，原有的 `TransitionPoint`（除了我们建的）：

- 门型（`isADoor`）：`SetIsInactive(true)`，不能交互。
- 边上的（`left1`、`bot1`……）：关掉它的触发碰撞体（走进去不再切场景），在它的位置放一块实心的空气墙（地形层，碰撞框 = 门的碰撞框），堵住出口。

只堵出口，不按镜头范围加墙：原场地里 Boss 战用到的地方（例如卢戈利的厨房上层、幽影的风琴台）可能在镜头锁定区外面。

### 1.5 交互物体（对应前作神居场景里没有长椅、NPC、宝箱）

这场战斗场景里的 `InteractableBase`（长椅、NPC 对话、记忆入口、拉杆、宝箱……）加载时 `Deactivate(false)`，除了：

- `TransitionPoint`（1.4 处理）；
- Boss 物体自己和它下面的（例如针姬本体上的 NPC 对话、沙克拉本体）；
- 表里 `arena.keep` 列出的路径：要玩家按“挑战”才开打的 Boss 保留挑战区（崇高圣母灵丝、斯卡尔歌后卡梅莉塔，`arena.challenge`）。卡梅莉塔的挑战区一关，她就一直在背景跳舞（`BG Dance` 等挑战区发 `CHALLENGE`），不会下场（0.9.0 冒烟测试）。

### 1.6 回程

- 打赢（`OnBossSceneComplete`）、放弃：快照拷回（不变），从雕像开的战：`playerData.bossReturnEntryGate` = 那尊雕像的回程门名，以 **`door_dreamReturn`** 为入口切回大厅；`GameManager.EnterHero` 把入口换成回程门（GameManager.cs:3313），游戏自己把人放在雕像前。万神殿打完、作废同理，回到那扇门前。
- 大厅（圣所）加载时（`Sanctum.Prepare`）给每尊雕像、每扇万神殿的门建一个门型回程门：`door_statue_<id>`、`door_pantheon_<id>`，位置 = 雕像左边 2 格 / 门左边 3.6 格（和原来 `PlaceAtStatue` 站的位置一样），朝右。
- 名录、自检开的战（没有雕像）：回到长椅（`RespawningHero`，不变）。
- 死亡：游戏自己的非致命死亡，在长椅上醒来（不变）。

### 1.7 万神殿

- 下一场照样以 `door_dreamEnter` 进（`BeginBoss`）。
- 休息点（真实的长椅房间）、带血带丝、束缚、计时、无伤判定都不变。

### 1.8 Boss 前的小怪（神居版不要）

要求：原版 Boss 出场前要先打几波小怪的，神居版一律去掉，进去就打 Boss（2026-11-24 定）。Boss 出场后它自己叫来的小怪（召唤、阶段里的杂兵）是 Boss 的招式，不动。两种做法，都写在表里，运行时不猜：

- **`BattleScene` 的波次**（`arena.skipWaves`，写要跳过的 `BattleWave` 物体路径）：加载时从所属 `BattleScene` 的 `waves` 表里拿掉。`BattleScene` 开战从 `waves[0]` 开始、清完一波按表开下一波，数剩下的敌人也只数表里的波（BattleScene.cs：`DoStartBattle` → `StartWave(0)`、`WaveEnd`、`DoCheckEnemiesNew`），所以拿掉以后开战第一波就是 Boss 那一波（原版的出场延迟 `startDelay` 照旧）。波次物体本身不删、也不靠关它（别的组件还引用它，例如卢戈利的 `ActivateIfPlayerdataFalse` 会在 `Start` 里重新激活它），只把它下面的敌人关掉；含 Boss 物体的波、拿掉后一波不剩的，不跳并报错。日志写 `跳过波次 / skip wave`。
- **FSM 自己管的前置小怪**：用 `goto` 补丁改跳转，从小怪段之前直接跳到 Boss 出场那一步。

| Boss | 原版 Boss 前 | 神居版 |
|---|---|---|
| 卢戈利 | 第 1 波两只小蟑螂厨子 | `skipWaves` 第 1 波 |
| 格洛 | 前 5 波泥沼怪、蚊子、摆锤陷阱 | `skipWaves` 第 1–5 波 |
| 鸦虫教父 | 前 5 波鸦人陪审员 | `skipWaves` 第 1–5 波 |
| 育母 | 前 3 波石板蝇 | `skipWaves` 第 1–3 波 |
| 失缚怨魂 | `Boss Scene::Control` 先打尸壳（First Slasher → P2） | `goto`：`Encountered Start` / `Boss Title` 的 FINISHED → `P3 Pause` |
| 卡梅莉塔 | `Battle Dance` 里开 4 波猎手，自己在背景跳舞 | `goto`：`Battle Roar End` 的 FINISHED → `Launch In Antic` |

查过、不用改的：壳王卡汗（入场门本来就在 Boss 房，前面 4 个房间的杂兵战不经过）；失心加蒙德（进场步骤已经关掉 `Pre Garmond`）；监工兄弟（`Intro Minions` 在开战时收到 `LEAVE` 离场，战斗中的小怪是 Boss 叫的）；其余 Boss 场景里波次以外的敌人都是 Boss 的召唤、陷阱或房间里本来就有的敌人。清单用 `tools/wave_scan.py` 从场景导出离线列出来（见第 3 节）。

## 2. 表的格式（`data/bosses.json` 每个 Boss 的 `arena` 段）

```jsonc
"entry": { "scene": "Organ_01" },          // 只剩要加载的场景名（附加场景 Boss 是宿主场景）
"arena": {
  "door": [84.4, 104.6],                    // 入场门位置（世界坐标）
  "face": "right",                          // 进场朝向：right / left（不写就不改）
  "wake": { "path": "Boss Scene", "fsm": "Control", "state": "Init" },
                                            // 开战判定：这个 FSM 在 state（多个用 | 分隔）里等过、又离开了 = 开战。
                                            // 可选 "via": "BATTLE HERO"：离开时的跳转事件要是它（多个用 | 分隔）才算，见下面。
                                            // 或者 { "battle": "Battle Scene" }：这个 BattleScene 开战了。只给日志和自检用，不影响游戏。
  "encountered": true,                      // 开战前把 flags.encountered 设成 true（走重打分支，没有初见演出）。默认 true
  "patch": [                                // 加载时（FSM 还没开始跑）对场景的改动 = 前作 Boss FSM 里的神居分支
    { "type": "start", "path": "...", "fsm": "Control", "state": "Battle Start" },   // 改起始状态
    { "type": "goto", "path": "...", "fsm": "Control", "from": "Idle", "event": "FINISHED", "to": "Roar" },
                                                                                     // 改跳转（from 写 * 是全局跳转）
    { "type": "var", "path": "...", "fsm": "Control", "name": "Skip Intro", "bool": true },
    { "type": "activate", "path": "..." }, { "type": "deactivate", "path": "..." }
  ],
  "skipWaves": [ "Battle Scene/Wave 1", "Battle Scene/Wave 2" ],
                                            // 跳过的 Boss 前小怪波次（BattleWave 物体路径），见 1.8
  "end": [ { "path": "Boss Scene/Phantom", "fsm": "Control", "state": "Death Start" } ],
                                            // 特殊结局：在这些状态最前面插 EndGGBossScene。写了 end 就不用 bosses 判胜
                                            // 可选 terminal: true：此处判胜后截断原版结局，只用于确认会进入结局的终点
  "enter": [ { "type": "register", "event": "GONG HIT", "delay": 0.5 } ],
                                            // 进场后（人站好、能操作时）按顺序执行，相当于前作 Dream Entry 的最后几步。
                                            // 能用 patch 做的不写在这里
  "keep": [ "Boss Scene/Intro Sequence/Challenge Region" ],   // 不关的交互物体
  "challenge": true,                        // 挑战位：放进来以后不自己开战，玩家按“挑战”才开打（自检会替玩家按）
  "noAdditive": [ "Greymoor_08_boss" ],     // 这一场不加载的附加场景：宿主场景按原条件还会带别的 Boss 场景时写
  "hudGrace": 45                            // 开战后 HUD 收着、人能操作，过多少秒叫回来（默认 2，0.5–120）
}
```

- `end[].terminal`（0.10.5）：默认 `false`，保留旧的“前插判胜动作”行为。仅丝母 `Boss Scene/Silk Boss` / `Phase Control` / `Death Hit` 设为 `true`：判胜动作插在这个状态最前面，原状态的其他动作照常执行（停攻击、清弹幕等，`ATTACK STOP` 会放开被丝线抓住的主角），去掉本状态的出口；真正进入这个状态时才清除这个 FSM 的全局跳转，战斗前段不受影响。不能在安装补丁时就清空全局事件，也不能禁用 FSM / BossSceneController 来截断结局，否则可能破坏战斗或结束协程。非布尔值（包括 `"true"` 字符串）视为判胜配置错误，该 Boss 禁止开战。只适用于明确的最终结局状态，不能用于换阶段或还需要完成正常死亡清理的其他 Boss。
- `required`（0.10.4，Codex 审计 0.10.3）：`patch` 和 `enter` 的每一项都可以写 `"required": true`，表示这一步不成功这场就打不了。
  `patch` 里的必需项：这一场的场景都来了（或者等附加场景到头了）还没打上，就按“场地出错”回大厅（`Arena.Unwinnable`），屏幕上提示、不记尝试；
  `enter` 里的必需项：`when` 等不到、或者这一步执行失败，同样按场地出错处理（`Arena.MarkFatal`），后面的进场步骤不再执行。
  不写或写 `false` 的照旧：没做到只记警告。`required` 不是布尔值时读表记警告、按 `false` 算；写在 `arena.enter` 以外的 FSM 步骤上也记警告（没有意义）。
  现在标了必需的：针姬（pinstress）进场的 `TEST`、`Accept` 两步和 `NO PINSTRESS` 的 `goto` 补丁（缺了就不会开打）。
- `hudGrace`：开战以后 HUD 收着、人能操作（`acceptingInput`、没交出控制、没死），连续这么多秒（不算暂停）就叫回来（`ModeController.CheckFightHud`）。原初罪者的神龛收起 HUD 以后没有地方再叫回来，靠默认 2 秒叫回。开场演出本来就收着 HUD、演完自己会叫回来、演出里人又能动的写长一点：崇高圣母灵丝的 `Challenge Cam` 和 `Intro Up` 发 OUT，原版到 `Silk Boss` 的 `Begin` 才发 IN（挑战以后约 20 秒，0.10.3 实测 20.5 秒），写 45。

- `noAdditive`：附加场景的加载器在 `OnEnable` / `Start` 里调 `TryTestLoad` 决定加不加载（反编译 SceneAdditiveLoadConditional.cs），比 `sceneLoaded` 早，加载时关物体来不及，所以在 `TryTestLoad` 前置里拦下。例如沙克拉：Greymoor_08 按原条件还加载荒沼翼主的备用地点 `Greymoor_08_boss`，荒沼翼主飞进她的 `Enemy Range`，她就去打荒沼翼主（同伴模式），不是和玩家切磋。战斗中真正附加加载了这一场以外的场景在 `sceneLoaded` 里记一行警告（`TryTestLoad` 返回 true 不等于加载了：别的加载器查 `otherLoaderBlacklist` 时也调它）；拦下的日志每场每个加载器只记一次。拦下以后，把它列进 `otherLoaderBlacklist` 的加载器会当它没在加载：Greymoor_08 只有沙克拉自己的加载器列了它，而那个是强制加载的，不受影响（场景导出核实）。
- `wake.via`：同一个等待状态还能经别的事件离开、那不是和玩家开打时写。沙克拉的 `Attack Enemies` 在 `Idle` 收到 `ENEMY` 是去打别的敌人（同伴模式），`BATTLE HERO` 才是切磋，写 `"via": "BATTLE HERO"`。事件对不上不算开战，这次等待作废，回到等待状态以后再等；日志记前几次。标记没插上、靠每帧看状态时，只能看最近一次跳转（`Fsm.LastTransition`）：它是等待状态自己的跳转才确认得了，确认不了不算开战（自检会报没开战，不会误报通过）。
- 开战判定的日志写离开等待状态的那次跳转的事件（`Fsm.LastTransition`，`DoTransition` 在切状态前设好），开战那一刻再记等待 FSM 里不属于它自己的物体变量（触发区存下的“谁进来了”在这里）。
- 路径都是场景里的层级路径（根物体开始）。一条补丁只在它所在的场景加载时生效；附加场景的补丁等附加场景加载时再打。整场战斗结束时还没找到的补丁路径记警告。
- 开战判定怎么看：加载时在 `wake` 的等待状态最前面插一个标记动作（`WakeMark`），进状态记“在等”，离开记“开战”。每帧看状态名会漏掉只待一帧的等待状态：人一落地就在触发区里，FSM 进来当帧就走了（0.9.0 全量自检：末代裁决者的 `Encountered`、炽焰之父的 `Dormant`）。标记进状态就做完，不挡 `FINISHED`；`FsmState.OnExit` 对进过的动作都调（做完的也调）；FSM 被关掉时退出状态不算开战，之前在等待状态里待过也作废（重新打开后从起始状态走到别的状态不算开战，要再进一次等待状态）。等待状态写了几个（`A|B`）时，A 走到 B 不算开战：`Fsm.SwitchState` 先退 A 再进 B，进 B 的标记把刚记的“离开”撤掉（炽焰之父落点在 Gate Range 里，`Set HP` 直接跳 `Close Gates`，不经过 `Dormant`，所以写 `Set HP|Dormant`；失心加蒙德人一出 `Can Start Range` 就从 `Dormant` 回 `Out of Start Range`，进来又回 `Dormant`，两个都是等玩家，写 `Out of Start Range|Dormant`）。每帧看状态留作后备。
- 模板 FSM（`PlayMakerFSM` 用了 `FsmTemplate`）：反序列化时已经初始化过，加载时补丁、结束动作、开战判定标记都打得上；但物体第一次激活时 `Awake` 按模板新建一份 `Fsm`（PlayMakerFSM.cs `InitTemplate`），加载时改的全丢（0.9.0 全量自检：加蒙德的 `Death Hit` 结束动作没生效）。`PlayMakerFSM.Awake` 后置里，这一场加载时改过的模板 FSM 醒来后重打一次（每一项单独兜住，一项出错不影响后面的）；加载时已经醒了的不会再 `Awake`，原来打的留着。插进去的动作不用另外初始化：每次进状态 `FsmState.ActivateActions` 都对动作调 `Init(this)` 再 `OnEnter`（反编译 FsmState.cs）。结束动作只认排在状态最前面、启用的 `EndGGBossScene`；多一个也不会重复判胜（`BossSceneController.EndBossScene` 有 `endedScene` 防重入）。
- 补丁、结束动作只在打上 / 插上了，或者在等模板 FSM 醒来时才“占用”这一项：先加载的场景里有同路径、却不是目标的物体（比如没有这个 FSM），后加载的场景照样会再找；找到了却没打上的原因留着，所有场景都来了以后报警告。加载时每一项单独兜住，一项抛异常不影响后面的。“在等模板 FSM 醒来”指这一场加载期间它还没 `Awake` 过（`PlayMakerFSM.Awake` 后置记下醒过的；不看物体激活没有，醒过的物体可能在 `sceneLoaded` 前又被关掉），醒来会按模板重建；加载时插上了、但还会重建的结束动作也算在等，醒来重插成功才算数；等的 FSM 醒来前被销毁了就不算在等。模板 FSM 醒来重打失败就放掉占用，在这一场已经准备过的别的场景里再找同路径的目标。`activate` / `deactivate` 补丁的成功标准是物体自己的开关（`activeSelf`），上层没开时注明。
- 判胜装不全就不打：写了 `end` 的，每一项都要插上或者在等还活着的模板 FSM 醒来（表里的几个结束点可能是不同路线各自的结局，缺一个，玩家走那条路线就卡住）；没写 `end` 的，控制器一个 Boss 血量都没登记，或者 `bossObjects` 里有名字没找到血量组件（双 Boss 只登记一个会提前判赢：这时控制器的判胜回调也不算胜利）。这一场的场景都准备过了就马上判；还有场景没来时，没找到的项等附加场景（进场后 12 秒，只数没暂停的时间）。人还没进场就收到这一场的判胜（结束机会已经用掉，`EndBossScene` 防重入）也算赢不了。都在进场以后报（进场途中回大厅会和游戏自己的切场景撞上），回大厅，不记这场和万神殿的尝试。读表时 `arena.end` 不是数组或者有一项缺 `path` / `state` 的，整条不能开战。
- 不记尝试的技术故障还有：进错场景、切场景卡住放弃、放弃后迟到的进场、去休息点到了别的场景或途中被游戏切了场景、万神殿换场还原失败。暂停中先后来了几次回大厅，有一次是场地出错就按场地出错算；这一场已经判定赢不了（还没报）时，不管怎么回大厅都按场地出错算。
- 补发“进入”：先补发 `BattleScene`，再看 FSM 的“进入”监听；进场步骤等 FSM 的第一轮补发做完再开始（最多等 1 秒没暂停的时间；第一轮出错或等不到记警告，照样执行进场步骤）。进场步骤暂停中不执行；等人就位（10 秒）只数没暂停的时间，进场步骤等 `when`（15 秒）按游戏时间算，暂停久了不会把等待耗完。一个监听收到“进入”后 FSM 当场跳走，这个状态里剩下的监听不再发。Boss 所在的附加场景晚到时，一直看到它准备好：每个物理帧醒来先看有没有新场景，有就重新收集 FSM、再看 3 秒，收集完再扫，扫完才判断停不停（场景在等物理帧时准备好也不会漏）。时间按物理帧算，暂停不走。
- 切场景超时：重试一次（换一个场次号，上一次加载建的控制器、开战标记、延后步骤都不再认），还不行就放弃：连战作废、束缚解开、快照留着。放弃是终止的：不再重试，迟到的场景加载完成也不开战，直接回大厅。
- 删掉的字段：`entry.gate`、`entry.spawn`、`entry.candidates`、`entry.verified`、整个 `dream` 段、整个 `setup` 段，还有只是备注、没人用的 `fixes`、`memoryScene`、`skip`、`return`、`status`、`actVerified`。读表时遇到不认识的字段一律记警告、不读。
- `debug` 段（自检用的条件反应、暂停秒杀、清小怪）不变。

## 3. 数据怎么来

- **离线脚本**（`tools/arena_draft.py`，读 `dump_fsm.py` 导出的场景文本）给每个 Boss 列：Boss 物体有没有 `HealthManager`、是不是特殊死亡；Boss FSM 里收到 `ZERO HP` 后进的状态、设击败标记的状态（`end` 的候选）；等玩家进入的触发区和它的范围（`wake`、`door` 的候选，对话、环境、特效类的触发区排到最后）。
- `door` 的草稿取开战触发区的中心（新版导出读世界范围，旧格式的导出只认 `BoxCollider2D`）。表里现在的 50 个门都是实机核对过的；最早的草稿还参考过旧版自检的实测落点和旧表的入口、`dream` / `setup` 段，核对完以后这些输入和转换代码都删掉了（`known_spawns.json` 一起删）。`enter`、`patch`、`keep`、`challenge` 不出草稿，要实打跟踪 FSM 以后手写。
- `end`：设击败标记的状态（游戏自己在这里认定 Boss 被打败）；没有击败标记的用 `ZERO HP` 进的状态。FSM 用模板（导出里没有状态）或者靠阶段控制收尾的 Boss，静态找不到，打开自检的 FSM 跟踪（`Debug.SelfTestTrace`）实打一遍，照倒下时走过的状态补。
- Boss 前的小怪：`tools/wave_scan.py` 列出每个 Boss 场景里 `BattleScene` 的波次（Boss 在第几波、前面几波是什么敌人，给 `skipWaves` 出草稿），再列出波次以外、不是 Boss 的敌人（FSM 自己管的前置小怪要人看 FSM 判断）。
- `dump_fsm.py` 给每个触发碰撞体写世界坐标的范围（`world x a..b y c..d`，按物体的位置、旋转、缩放换算），多边形、边碰撞体不超过 12 个点的再列出各顶点：多边形可能是凹的，只看范围会把门放进缺口里（机枢拍掌者的 Battle Scene 底部中间开了倒 V 口）。范围是加载时的；等待状态里会挪动 Boss 的（边陲守望者 `Refight Ready` 先 `Translate` 15 格，挂在它身上的 Close Range 跟着走）要按 FSM 加上。
- 我核对后写进表；新版自检逐个验证：从入场门进来、开战（`wake`）、判胜方式（`bosses` / `end`）、回到雕像前。

## 4. 不做的（可选）

- 前作按难度改 Boss 血量（`HPScaleGG`）：本作没有这段代码，要补丁 `HealthManager`。先不做，Boss 保持原版血量。
- 前作神居的布景（观众、柱子）：场地外观保持原版。
