# Boss 清单（阶段 0 初稿）

> 数据来源：游戏本体 1.0.30000（Steam buildid 22479045，Unity 6000.0.50f1）。
> 名字全部取自游戏自带的中英文本地化表（标题卡 `Titles` 和猎人图鉴 `Journal`），场景、出口、血量、加载条件取自对 590 个场景包的静态扫描。
> 机读版本在 `data/bosses.json`，本文件是给人看的说明。两者不一致时以 `bosses.json` 为准。

## 怎么得出来的

1. **名单**：猎人图鉴里击杀数为 1 的条目 + 标题卡里带 Boss 名的键，两边对照，去掉普通杂兵。
2. **场景**：扫描每个场景里的 `HealthManager`（血量）、`DisplayBossTitle` 用的标题键、FSM 里引用的 `PlayerData` 标记，三者对上的就是 Boss 房。
3. **附加场景**：很多 Boss 不在主场景里，而是由主场景的 `SceneAdditiveLoadConditional` 按条件加载一个 `_boss` 场景（例如 `Bone_05` → `Bone_05_boss`）。加载条件也从扫描里读出来了，写在 `bosses.json` 的 `additiveLoader` 里。
4. **幕**：游戏数据里没有“这个 Boss 属于第几幕”的字段，表里的幕是按常规流程推定的。

## 字段说明（`data/bosses.json`）

0.9.0 起战斗走前作神居的做法（原场景在加载时改成神居版，从 `door_dreamEnter` 进、从 `door_dreamReturn` 回），场地相关的字段全在 `arena` 段，见 [`arena.md`](arena.md)。

| 字段 | 含义 |
|---|---|
| `id` | 我们自己的 Boss 编号，代码和存档里都用它 |
| `name.zh / name.en` | 游戏官方中英文名 |
| `variant` | 同一个 Boss 打多次时的区分（我们自己起的名字） |
| `tier` | `boss` / `mini`（小 Boss，没有标题卡）/ `spar`（NPC 切磋） |
| `act` | 推定的幕 |
| `region` | 场景所属地区（`CustomSceneManager.mapZone` → `Map Zones` 表） |
| `scene` | Boss 实际所在的场景 |
| `hostScene`, `additive` | Boss 在附加场景里时：主场景名；战斗时强制加载附加场景（不管原来的剧情条件） |
| `entry.scene` | 要加载的场景（一般就是 `scene`，附加场景 Boss 是宿主场景）。入口一律是场地的 `door_dreamEnter` |
| `bossObjects` | Boss 物体名。`arena.end` 没写时，场地控制器按这些名字找血量组件登记成 `BossSceneController.bosses` |
| `flags.defeated` | 击败标记（`PlayerData` 字段名），只给日志和自检看，不参与判胜 |
| `flags.encountered` | “见过”标记：开战前在快照里设成 `true`，走重打分支（`arena.encountered: false` 关掉） |
| `prerequisites.playerData` | **前置剧情标记**：进场前在快照环境里临时设置的 `PlayerData` 值 |
| `allowScenes` | 战斗中允许切去的场景（多场景的 Boss 战）。切到别的场景都改道回大厅，记作放弃。目前表里没有 Boss 用到它 |
| `arena` | **神居场地**：入场门位置 `door`、朝向 `face`、加载时补丁 `patch`、特殊结局 `end`、进场步骤 `enter`、不关的交互物体 `keep`、挑战位 `challenge`、开战判定 `wake`（可写离开时的事件 `via`）、这一场不加载的附加场景 `noAdditive`。见 [`arena.md`](arena.md) 第 2 节 |
| `journal`, `statueArt` | 只给长廊雕像找立绘用，不参与判胜 |
| `debug.kill` | **只在调试自检里用**：对没有血量组件的 Boss 的“击杀”步骤 |
| `debug.timeout / killInterval` | 自检这个 Boss 最多等几秒（默认用配置 `Debug.SelfTestStepTimeout`）；一击打空后隔几秒再打下一击（默认 5，让倒地 / 换阶段演出走完） |
| `debug.killAdds` | `true` 时自检顺便打掉战斗场景里不是 Boss 的敌人；写成路径数组时只打这些路径下的（场景要先清小怪才推进阶段的 Boss，例如失缚怨魂） |
| `debug.react` | 条件反应：FSM 步骤加一个 `when`（多个状态用 `\|` 分隔），那个 FSM 正停在 `when` 状态、且停了 `after` 秒（默认 0）时才执行；每帧检查，同一步 1 秒内一次。例如幽影最后一段停在 `Parry Ready` 时替玩家发 `PARRY` |
| `debug.holdKill` | 暂停秒杀的条件：`{path, fsm, when}`，那个 FSM 停在 `when` 状态时不打 Boss（自检的伤害不需要碰撞体，Boss 休眠、等场景时玩家本来打不到它） |
| FSM 步骤 | `{"type":"event","path":"物体路径","fsm":"FSM 名","event":"事件"}`；`type` 也可以是 `state`（切到 `state`）、`register`（`EventRegister` 全局广播 `event`）、`activate` / `deactivate`（打开 / 关闭 `path` 物体）；可选 `delay` 秒；`debug.react` 里另加 `when`、`after`；`arena.enter` 里的 `when` 是先等那个 FSM 进到这个状态 |
| `titleKey`, `hp`, `additiveLoader`, `notes` | **只是备注**：标题卡本地化键、原版血量、附加场景的原版加载条件、说明 |

**读表规则**（`Data/BossCatalog.cs`）：整个文件读不了时表为空；单条有问题只跳过那一条，日志里写第几条、为什么，其余照常可用。会被跳过的：缺 `id` 或 `scene`、`id` 和前面的重复、字段类型不对导致读取出错。不认识的字段（顶层、`entry`、`arena`、`debug` 里）记警告、不读。FSM 步骤里不认识的 `type`、`type` 写了但不是字符串、`debug.react` / `holdKill` 里没写 `when` 的步骤，`arena.patch` / `arena.end` 里缺必填项的，记警告后只跳过那一项。`flags.defeated` / `flags.encountered` 里写错的字段名、不是 bool 的字段，`prerequisites.playerData` 里写错的字段名，读完后在日志里列出来，并从那一条里去掉；这三处都不许写存档身份字段 `bossRushMode`（神居存档的标记，开战后改掉它会让模式重置丢掉快照），写了也去掉。判胜相关的写错了不跳过、整条不能开战：`arena.end` 不是数组，或者有一项缺 `path` / `state`（跳过会悄悄改成按 `bosses` 判胜，特殊死亡的 Boss 就赢不了）。`arena.encountered` 不是 true / false 时记警告、按默认 true；`arena.patch` 的 `var` 只能写 `bool` / `int` / `float` / `string` 中的一个，值的类型要对得上，不然跳过这条补丁。`id` 只在那一条解析成功后才算占用；`entry.scene` 写成空字符串等于没写（用 `scene`）；`act` 只认 0–99 的整数。数值都不认 NaN / 无穷大，并限定范围：步骤的 `delay`、`after` 0–60 秒，`debug.timeout` 0–900 秒，`debug.killInterval` 0.5–60 秒。没有 `arena.door` 的 Boss 不能开战（运行时不猜），读表时列出来。

## 总数

- Boss：44 场（其中同名多战：苔藓之母 ×2、蕾丝 ×2（另有失心蕾丝）、残暴的兽蝇 ×2、颅骨暴君 ×2）
- 小 Boss：3（悬峭兽、机枢击钹者、巨蚤）
- 切磋：3（加蒙德 & 扎扎、沙克拉；另外加蒙德 & 扎扎的标记语义特殊）

不收录的：普通杂兵、钟道站的 `Bellway_0x_boss` 附加场景（是快速旅行用的，不是 Boss）、`Hang_04_boss`（高庭的圣咏槌手三连战，属于竞技场战斗，没有标题卡；需要的话阶段 3 可以加进连战）。

## 清单

### 第一幕（推定）

| id | 名称 | 英文名 | 类型 | 地区 | 场景 | 入场门 door_dreamEnter | 血量 | 击败标记 |
|---|---|---|---|---|---|---|---|---|
| `moss_mother` | 苔藓之母（初遇） | Moss Mother (First Encounter) | Boss | 骸底镇 | `Tut_03` | `Tut_03 @ (54.1, 18.6)` | 120 | `defeatedMossMother` |
| `bell_beast` | 钟道兽 | Bell Beast | Boss | 髓骨洞窟 | `Bone_05_boss<br>← Bone_05` | `Bone_05 @ (85.5, 4.6)` | 150 | `defeatedBellBeast` |
| `lace_1` | 蕾丝（初次交锋） | Lace (First Duel) | Boss | 深坞 | `Bone_East_12` | `Bone_East_12 @ (95.8, 8.6)` | 250 | `defeatedLace1` |
| `fourth_chorus` | 第四圣咏团 | Fourth Chorus | Boss | 远野 | `Bone_East_08_boss_golem<br>← Bone_East_08` | `Bone_East_08 @ (78.0, 8.1)` | 500 | `defeatedSongGolem` |
| `savage_beastfly` | 残暴的兽蝇（野兽教堂） | Savage Beastfly (Chapel of the Beast) | Boss | 猎者小径 | `Ant_19` | `Ant_19 @ (49.6, 35.6)` | 550 | `defeatedBoneFlyerGiant` |
| `sister_splinter` | 碎裂者长姊 | Sister Splinter | Boss | 壳木林 | `Shellwood_18` | `Shellwood_18 @ (51.4, 9.6)` | 310 | `defeatedSplinterQueen` |
| `skull_tyrant` | 颅骨暴君（髓骨洞窟） | Skull Tyrant (The Marrow) | Boss | 髓骨洞窟 | `Bone_15` | `Bone_15 @ (99.6, 15.6)` | 450 | `skullKingDefeated` |
| `widow` | 黑寡妇 | Widow | Boss | 钟心镇 | `Belltown_Shrine` | `Belltown_Shrine @ (54.4, 9.6)` | 360 | `spinnerDefeated` |
| `great_conchflies` | 巨型螺蝇 | Great Conchflies | Boss | 风蚀长阶 | `Coral_11` | `Coral_11 @ (61.8, 15.6)` | 400 | `defeatedCoralDrillers` |
| `moorwing` | 荒沼翼主 | Moorwing | Boss | 灰沼 | `Greymoor_05_boss<br>← Greymoor_05` | `Greymoor_05 @ (50.8, 6.6)` | 600 | `defeatedVampireGnatBoss` |
| `phantom` | 幽影 | Phantom | Boss | 圣堡 | `Organ_01` | `Organ_01 @ (84.4, 105.6)` | 650 | `defeatedPhantom` |
| `last_judge` | 末代裁决者 | Last Judge | Boss | 风蚀长阶 | `Coral_Judge_Arena` | `Coral_Judge_Arena @ (53.3, 30.7)` | 720 | `defeatedLastJudge` |
| `forebrothers` | 监工兄弟西格尼斯 & 格隆 | Forebrothers Signis & Gron | Boss | 深坞 | `Dock_09` | `Dock_09 @ (26.5, 8.6)` | 720 / 520 | `defeatedDockForemen` |
| `gurr` | 被放逐的格尔 | Gurr The Outcast | Boss | 远野 | `Bone_East_18b` | `Bone_East_18b @ (170.2, 7.4)` | 1000 | `defeatedAntTrapper` |
| `craggler` | 悬峭兽 | Craggler | 小 Boss | 髓骨洞窟 | `Crawl_04` | `Crawl_04 @ (79.5, 5.0)` | 200 | `roofCrabDefeated` |

### 第二幕（推定）

| id | 名称 | 英文名 | 类型 | 地区 | 场景 | 入场门 door_dreamEnter | 血量 | 击败标记 |
|---|---|---|---|---|---|---|---|---|
| `lugoli` | 失格大厨卢戈利 | Disgraced Chef Lugoli | Boss | 罪途 | `Dust_Chef` | `Dust_Chef @ (41.9, 36.6)` | 600 | `defeatedRoachkeeperChef` |
| `groal` | 伟大的格洛 | Groal The Great | Boss | 腐汁泽 | `Shadow_18` | `Shadow_18 @ (56.5, 12.5)` | 650 | `DefeatedSwampShaman` |
| `crawfather` | 鸦虫教父 | Crawfather | Boss | 灰沼 | `Room_CrowCourt_02` | `Room_CrowCourt_02 @ (35.2, 22.6)` | 1300 | `defeatedCrowCourt` |
| `father_of_the_flame` | 炽焰之父 | Father of the Flame | Boss | 火灵竹丛 | `Belltown_08` | `Belltown_08 @ (59.0, 10.0)` | ? | `defeatedWispPyreEffigy` |
| `voltvyrm` | 伏特维姆 | Voltvyrm | Boss | 卡拉卡沙川 | `Coral_29` | `Coral_29 @ (170.0, 25.6)` | 550 | `defeatedZapCoreEnemy` |
| `raging_conchfly` | 狂暴的螺蝇 | Raging Conchfly | Boss | 卡拉卡沙川 | `Coral_27` | `Coral_27 @ (16.3, 34.6)` | 820 | `defeatedCoralDrillerSolo` |
| `broodmother` | 育母 | Broodmother | Boss | 罪石牢狱 | `Slab_16b` | `Slab_16b @ (67.5, 7.5)` | 700 | `defeatedBroodMother` |
| `first_sinner` | 原初罪者 | First Sinner | Boss | 罪石牢狱 | `Slab_10b` | `Slab_10b @ (38.3, 9.5)` | 1300 | `defeatedFirstWeaver` |
| `cogwork_dancers` | 机枢舞者 | Cogwork Dancers | Boss | 机枢核心 | `Cog_Dancers_boss<br>← Cog_Dancers` | `Cog_Dancers @ (39.9, 5.5)` | 300 / 300 | `defeatedCogworkDancers` |
| `trobbio` | 特罗比奥 | Trobbio | Boss | 圣堡 | `Library_13` | `Library_13 @ (69.5, 15.6)` | 700 | `defeatedTrobbio` |
| `the_unravelled` | 失缚怨魂 | The Unravelled | Boss | 白愈厅 | `Ward_02_boss<br>← Ward_02` | `Ward_02 @ (44.0, 8.9)` | 1000 | `wardBossDefeated` |
| `second_sentinel` | 次席戍卫 | Second Sentinel | Boss | 高庭 | `Hang_17b` | `Hang_17b @ (44.5, 7.5)` | 800 | `defeatedSongChevalierBoss` |
| `lace_2` | 蕾丝（高塔之战） | Lace (Tower Duel) | Boss | 摇篮圣所 | `Song_Tower_01` | `Song_Tower_01 @ (59.2, 102.8)` | 800 | `defeatedLaceTower` |
| `grand_mother_silk` | 崇高圣母灵丝 | Grand Mother Silk | Boss | 摇篮圣所 | `Cradle_03` | `Cradle_03 @ (42.2, 134.6)` | 1204 | `待查` |
| `garmond_zaza` | 加蒙德 & 扎扎 | Garmond And Zaza | 切磋 | 圣堡 | `Library_09` | `Library_09 @ (73.8, 16.7)` | 460 | `garmondWillAidInForumBattle` |
| `cogwork_clapper` | 机枢击钹者 | Cogwork Clapper | 小 Boss | 圣堡 | `Cog_07` | `Cog_07 @ (21.5, 72.0)` | 190 / 190 | `待查` |
| `shakra` | 沙克拉 | Shakra | 切磋 | 灰沼 | `Greymoor_08_mapper<br>← Greymoor_08` | `Greymoor_08 @ (37.4, 7.0)` | 600 | `待查` |
| `savage_beastfly_2` | 残暴的兽蝇（远野再临） | Savage Beastfly (Far Fields Return) | Boss | 远野 | `Bone_East_08_boss_beastfly<br>← Bone_East_08` | `Bone_East_08 @ (81.0, 15.8)` | 650 | `defeatedBoneFlyerGiantGolemScene` |

### 第三幕（推定）

| id | 名称 | 英文名 | 类型 | 地区 | 场景 | 入场门 door_dreamEnter | 血量 | 击败标记 |
|---|---|---|---|---|---|---|---|---|
| `clover_dancers` | 三叶草舞者 | Clover Dancers | Boss | 翠庭 | `Clover_10` | `Clover_10 @ (85.0, 39.5)` | 300 / 300 | `defeatedCloverDancers` |
| `palestag` | 苍白苜鹿 | Palestag | Boss | 翠庭 | `Clover_19` | `Clover_19 @ (31.1, 18.1)` | 480 | `defeatedWhiteCloverstag` |
| `nyleth` | 尼莱斯 | Nyleth | Boss | 忆境 | `Shellwood_11b_Memory` | `Shellwood_11b_Memory @ (18.0, 109.5)` | 1250 | `defeatedFlowerQueen` |
| `crust_king_khann` | 壳王卡汗 | Crust King Khann | Boss | 忆境 | `Memory_Coral_Tower` | `Memory_Coral_Tower @ (50.6, 558.1)` | 1650 | `defeatedCoralKing` |
| `karmelita` | 斯卡尔歌后卡梅莉塔 | Skarrsinger Karmelita | Boss | 忆境 | `Memory_Ant_Queen` | `Memory_Ant_Queen @ (149.0, 21.1)` | 1500 | `defeatedAntQueen` |
| `seth` | 圣所守卫者赛斯 | Shrine Guardian Seth | Boss | 壳木林 | `Shellwood_22` | `Shellwood_22 @ (104.8, 10.5)` | 1185 | `defeatedSeth` |
| `lost_garmond` | 失心加蒙德 | Lost Garmond | Boss | 风蚀长阶 | `Coral_33` | `Coral_33 @ (23.7, 66.5)` | 900 | `garmondBlackThreadDefeated` |
| `plasmified_zango` | 生质异化的赞戈 | Plasmified Zango | Boss | 沙噬虫道 | `Crawl_10` | `Crawl_10 @ (16.8, 8.0)` | 1000 | `待查` |
| `pinstress` | 针姬 | Pinstress | Boss | 雪灵山 | `Peak_07` | `Peak_07 @ (38.0, 87.4)` | 910 | `待查` |
| `watcher_at_the_edge` | 边陲守望者 | Watcher at the Edge | Boss | 卡拉卡沙川 | `Coral_39` | `Coral_39 @ (137.0, 9.5)` | 900 | `defeatedGreyWarrior` |
| `bell_eater` | 噬钟者 | Bell Eater | Boss | 钟心镇 | `Bellway_Centipede_Arena` | `Bellway_Centipede_Arena @ (146.5, 8.0)` | 400 / 400 | `待查` |
| `summoned_saviour` | 蒙召救主 | Summoned Saviour | Boss | 髓骨洞窟 | `Bone_Steel_Servant` | `Bone_Steel_Servant @ (26.5, 6.5)` | 500 | `待查` |
| `tormented_trobbio` | 痛苦的特罗比奥 | Tormented Trobbio | Boss | 圣堡 | `Library_13` | `Library_13 @ (69.5, 15.6)` | 950 | `defeatedTormentedTrobbio` |
| `skull_tyrant_invasion` | 颅骨暴君（入侵骸底镇） | Skull Tyrant (Bone Bottom Invasion) | Boss | 骸底镇 | `Bonetown_boss<br>← Bonetown` | `Bonetown @ (235.6, 9.0)` | 450 | `skullKingKilled` |
| `moss_mother_duo` | 苔藓之母（织巢双生） | Moss Mother (Weavenest Pair) | Boss | 阿特拉织巢 | `Weave_03` | `Weave_03 @ (4.3, 21.6)` | 350 / 350 | `待查` |
| `huge_flea` | 巨蚤 | Huge Flea | 小 Boss | 圣堡 | `Arborium_08` | `Arborium_08 @ (33.7, 34.5)` | 200 | `tamedGiantFlea` |
| `lost_lace` | 失心蕾丝 | Lost Lace | Boss | 深渊 | `Abyss_Cocoon` | `Abyss_Cocoon @ (46.0, 8.0)` | 1800 | `待查` |

## 难点

按“做这个模式时会遇到什么麻烦”分类。一个 Boss 可能同时属于几类。

### A. 附加场景 Boss（要强制加载）

Boss 房是主场景按条件加载的附加场景。重打时条件一般不满足（例如已经击败过），需要在快照环境里改 `PlayerData`，或者直接由 Mod 加载附加场景。

| Boss | 主场景 → 附加场景 | 原版加载条件 |
|---|---|---|
| 钟道兽 | `Bone_05` → `Bone_05_boss` | `defeatedBellBeast == false` |
| 第四圣咏团 | `Bone_East_08` → `Bone_East_08_boss_golem` | `defeatedSongGolem == false` 且 `hasBrolly == true`；另有 `_rest` 场景是战前休眠状态 |
| 残暴的兽蝇（远野） | `Bone_East_08` → `Bone_East_08_boss_beastfly` | 任务条件：已承接、未完成 |
| 荒沼翼主 | `Greymoor_05` → `Greymoor_05_boss` | 加载器没有条件（由 FSM 控制）；另有备用地点 `Greymoor_08_boss` |
| 机枢舞者 | `Cog_Dancers` → `Cog_Dancers_boss` | 加载器没有条件（由 FSM 控制） |
| 失缚怨魂 | `Ward_02` → `Ward_02_boss` | `WillLoadWardBoss == true` |
| 颅骨暴君（入侵） | `Bonetown` → `Bonetown_boss` | `skullKingKilled == false`、`skullKingWillInvade == true`、`blackThreadWorld == false`（还有第二组条件） |
| 沙克拉（切磋） | `Greymoor_08` → `Greymoor_08_mapper` | 待查 |

### B. 忆境场景（进出流程特殊）

入口是 `door_wakeInMemory`，地图区域是 `MEMORY`。代码里有 `GameManager.IsMemoryScene`、`QueueMemoryFullHeal` 等专门逻辑，退出时走 `door_memoryEnd`。需要单独实现进出。

- 尼莱斯 `Shellwood_11b_Memory`
- 壳王卡汗 `Memory_Coral_Tower`：Boss 前有 4 个房间的杂兵战（`Battle Scene Chamber 1~4`），重打要么直接传到 Boss 房，要么跳过波次。这个场景包有 46MB，加载最慢。
- 斯卡尔歌后卡梅莉塔 `Memory_Ant_Queen`

### C. 打完会进结局 / 过场 / 记忆演出（必须拦截）

- 崇高圣母灵丝 `Cradle_03`：第二幕结局。
- 失心蕾丝 `Abyss_Cocoon`：第三幕最终 Boss，出口只有自指的 `door_entry` / `door_test`。
- 蕾丝（高塔之战）`Song_Tower_01`：打完接丝之心记忆和过场（`door_cutsceneEndLaceTower`、`door_cinematicEnd`）。
- 钟道兽、失缚怨魂：所在主场景有 `Silk Heart` / `Silk Heart Memory Return`，打完会接丝之心记忆。
- 黑寡妇：有假死状态（`Fake Death`），打完给织忆弦针并推进钟心镇剧情。
- 残暴的兽蝇（野兽教堂）、原初罪者：出口有 `door_memoryEnd` / `door_wakeOnGround`，和纹章或剧情演出连在一起。

### D. 杂兵波次 / 竞技场

场景里有 `BattleScene` 物体，Boss 前或战斗中有杂兵：失格大厨卢戈利、伟大的格洛（`Boss Summon Crew`）、鸦虫教父、育母、狂暴的螺蝇、蒙召救主、苔藓之母（两场）、卡梅莉塔、卡汗。阶段 1 逐个确认哪些是 Boss 战本身的一部分（保留）、哪些是前置杂兵（跳过）。

### E. 多 Boss / 多部件

- 双 Boss：监工兄弟（720 / 520）、机枢舞者、三叶草舞者、苔藓之母（织巢双生）、巨型螺蝇（静态扫描只看到一只，第二只可能运行时生成）。“击败”要等所有 Boss 都死。
- 噬钟者：头、尾各 400 血，中间体节是 hp 10000 的不可击杀部件；出口是动态门 `[dynamic]:door_centipedeExit`。
- 颅骨暴君：场景里有 hp 9999999 的 `Pierce Dmg Receiver`，统计时排除。
- 机枢击钹者：三个球体。

### F. 同场景多 Boss（靠标记切换）

- 特罗比奥 / 痛苦的特罗比奥 都在 `Library_13`，靠剧情标记决定出场的是谁。
- 次席戍卫：专用场景 `Hang_17b`，但 `Hang_07` 里也有一个 `Song Knight`，可能是另一种出场方式。

### G. 静态扫描没找到 Boss 本体 / 击败标记

- 炽焰之父 `Wisp_07`：只找到 `defeatedWispPyreEffigy` 标记，没找到 Boss 的血量组件，Boss 可能是运行时生成的预制体。**阶段 1 必须实机确认。**
- 击败标记待查：崇高圣母灵丝、失心蕾丝、生质异化的赞戈、针姬、噬钟者、蒙召救主、苔藓之母（织巢双生）。（`PlayerData` 里没有直接对应的 `defeatedXxx`，可能用的是任务、图鉴击杀数或别的字段。）

### H. 切磋 / NPC 战

- 加蒙德 & 扎扎 `Library_09`：标记 `garmondLibraryDefeatedHornet` 的意思是“加蒙德打赢了大黄蜂”，胜负条件和普通 Boss 相反。
- 沙克拉 `Greymoor_08_mapper`：NPC 切磋，触发条件待查。

### I. 第三幕“黑线世界”

不少场景检查 `blackThreadWorld`（第三幕黑线侵蚀后的世界状态），会换掉 Boss 或场景内容。快照环境里要明确设定这个值，否则同一个 Boss 在第二幕存档和第三幕存档里进去看到的东西不一样。

### J. 入场门位置

0.9.0 起不再从原场景的出口进来：每个 Boss 的场地在加载时建一个门型入口 `door_dreamEnter`，位置写在 `arena.door`（见 [`arena.md`](arena.md)）。位置先由离线工具按场景导出出草稿（`tools/arena_draft.py`），再逐个实机核对（0.9.0 全量自检）。

## 待确认问题

1. 每个 Boss 的 `arena` 段（入场门位置、加载时补丁、特殊结局），见 [`arena.md`](arena.md) 第 3 节。
2. 每个 Boss 的幕归属（表里是推定的）。
