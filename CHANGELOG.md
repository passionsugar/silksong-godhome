# Changelog

Newest first. Release archives and full notes: https://github.com/passionsugar/silksong-godhome/releases

## 0.10.8 (unreleased)

- Reworked all five Pantheon routes, added Cogwork Clapper to Pantheons II and V, and adjusted health for 14 boss encounters.
- Updated Needle and Silk bindings, tool supplies and rest benches. Fixed arena boundaries, boss introductions and Phantom's phase threshold.
- Improved Hall of Gods statue placement and return interactions, cached statue artwork across game launches, and fixed hub checkpoints and save-slot presentation.

## 0.10.7 (2026-11-27)

Fixes and changes from player testing, update recommended. Saves need no changes.

Hall of Gods (Hollow Knight Godhome):

- **Every boss has its own statue.** Seven pairs of unrelated bosses shared a statue, so its lever switched one boss into another. They are split up: the hall now has 43 statues, seven of them copies of Hollow Knight statues placed in empty spots. A lever only switches between forms of the same boss.
- **Levers on every boss with more than one form,** and none on single-form bosses.
- **Single-form statues show one plaque** instead of two.
- **The red glow beside the pedestal** is lit only for the second and later forms, and fades in and out when you switch, as in Hollow Knight.
- **Statue sizes follow the bosses' real sizes** on one common scale. Very tall or wide ones are capped to fit the hall.
- **Statues stay on their pedestals** while the lever lowers and raises them, and no longer jump back before the next form rises.
- **No input after falling out of the map.** If Hornet takes no input for 2 seconds while nothing (cutscene, menu, bench) has taken control, control is restored, the same as pausing and resuming.

Pantheons:

- **Silk skills are no longer blocked by the tool binding,** like spells under Hollow Knight's charm binding.
- **The spool binding redraws the silk spool** at its bound length, so the remaining capacity is visible.
- **Mask binding raised from 4 to 5 masks.** Many Silksong attacks deal two.
- **The binding icons on the pantheon doors** are Silksong's needle, mask, tool and spool icons instead of Hollow Knight's.

修复和调整版本，建议更新，存档不用动。

手办屋（前作神居）：

- **每个 Boss 有自己的雕像**：以前有 7 对不相干的 Boss 共用一尊雕像，推杆会把一个 Boss 换成另一个。现在全部拆开，手办屋共 43 尊，其中 7 尊照前作的雕像复制，放在空位上。推杆只切换同一个 Boss 的不同形态。
- **有多个形态的 Boss 都有推杆**，只有一个形态的没有。
- **单形态的雕像只摆一块牌子**，不再是两块。
- **底座旁的红色光晕**只在第二及以后的形态亮，切换时淡入淡出，和前作一样。
- **雕像按 Boss 的真实大小**、统一比例；特别高或特别宽的压到放得下。
- **推杆升降时雕像不再偏离底座**，下次切换也不会先瞬移回原位。
- **掉出地图后动不了**：没有演出、菜单、长椅等占用控制，却 2 秒不接输入时，自动恢复操作（和暂停再继续一样）。

万神殿：

- **工具束缚不再挡丝技**，和前作护符束缚不挡法术一样。
- **灵丝束缚时丝轴按受限后的长度重画**，看得清实际容量。
- **面具束缚从 4 格改为 5 格**：丝之歌里一次扣两格的攻击很多。
- **万神殿门上的束缚图标**换成丝之歌的织针、面具、工具、丝轴。

## 0.10.6 (2026-11-27)

Fixes from player reports, update recommended. Saves need no changes.

- **Frozen at the start of each pantheon's first fight.** The Hollow Knight challenge door tells Hornet to enter the next room without input, and Hollow Knight hands control back in a cutscene that Godhome skips. You now have control as soon as you land.
- **Cross Stitch equipped by itself after Phantom.** Phantom's defeat grants the parry and equips Cross Stitch; in a pantheon that loadout carried into every later fight. Loadout changes the game makes during a fight are no longer carried over. Changes you make at a rest bench still are.
- **Clover Dancers ended after the first phase.** The win now waits for the second phase.
- **Savage Beastfly (Far Fields rematch) hovering at the side without attacking.** The entry point is inside its wake range, which cut its opening setup short. It now finishes the setup and chases you.
- **Roof of the Hollow Knight Godhome.** Once the first four pantheons are done, strike the lever above the fifth door to open the leaves, as in Hollow Knight. The exits to and from the roof were on a physics layer that Silksong's Hornet never touches, so jumping in did nothing. They work now.

修复版本，建议更新，存档不用动。

- **每个万神殿第一场开始时动不了**：前作的挑战门让大黄蜂“不接输入地进下一个房间”，前作靠一段演出交还操作，神居跳过了那段演出。现在落地就能操作。
- **打完幽影自动装上十字缝**：幽影倒下时游戏给了弹反、自动装上十字缝，万神殿里这套配装一直带到后面各场。现在战斗中游戏自己改的配装不带到下一场；休息点长椅上自己换的照旧带。
- **三叶草舞者第一阶段打完就判胜**：现在要等第二阶段打完。
- **残暴的兽蝇（远野再临）飞到一边停着不打**：入场点在它的开战范围里，开场设置没做完就开打了。现在设置做完、会追着人打。
- **前作神居的屋顶**：前四个万神殿完成后，照前作用骨钉敲第五扇门上方的拉杆，打开叶子。去屋顶和从屋顶回来的出口原来在丝之歌的大黄蜂碰不到的物理层上，跳进去没反应，现在能用了。

## 0.10.5 (2026-11-27)

Bug fixes, update recommended. Saves need no changes.

- **Floating after Grand Mother Silk.** After the win you could stop falling: jumping kept rising, only the glide brought you down. The original ending sequence makes Hornet's body kinematic and hides her, and Godhome left the arena before that was undone. Godhome now ends the fight at her Death Hit, before the ending sequence (her own stop-attack and clear actions still run). Leaving any fight puts Hornet's body, gravity and visibility back. In the lobby, a body left kinematic is fixed as soon as you have control.
- **Rosaries and shell shards no longer run out in the Godhome save.** Rosaries stay at 9999 and shell shards at the game's cap. Spending is waived, so refilling the Rosary Cannon and the Architect crest's crafting can be used freely. Normal saves are not affected, and the tool binding in pantheons works as before.
- **Moss Mother (Weavenest Pair) entry.** The entry point was outside the arena wall and could leave you stuck outside the map. You now arrive on the arena floor. Hazard respawns and retries after a death land on the same spot.

修复版本，建议更新，存档不用动。

- **打完崇高圣母灵丝后浮空**：落不下来，按跳一直往上升，只有滑翔能往下落。原版结局演出会把大黄蜂的刚体设成运动学并隐藏她，神居在还原之前就离开了战场。现在在她的 Death Hit 判胜，不再进入结局演出（她自己的停攻击、清弹幕照常执行）。离开任何一场战斗时都把大黄蜂的刚体、重力和显示改回来；大厅里刚体还是运动学的，一能操作就修好。
- **神居存档里念珠、工具碎片用不完**：念珠保持 9999，碎片保持游戏上限。花费全部免除，念珠炮补弹药、建筑师纹章的现场打造可以随便用。普通存档不受影响，万神殿的工具束缚照旧生效。
- **苔藓之母（织巢双生）入场点**：以前在场地墙外，可能被卡在地图外面。现在直接落在场内地面；危险复活和死亡后重试也落在同一个位置。

## 0.10.4 (2026-11-26)

One-click installer (`install.bat`); fixes from a code audit of 0.10.3: pantheon crest and tool changes at rest benches now last the whole run, blue masks carry over between fights, on-screen notices for errors, the Hollow Knight Godhome victory transition can no longer hang, timer display, Pinstress entry steps.

一键安装（`install.bat`）；按 0.10.3 的代码审计修复：万神殿休息点换的纹章、工具带到连战结束，蓝色面具照实带到下一场，出错时屏幕提示，前作神居胜利转场不再卡住，计时显示，针姬进场步骤。

## 0.10.3 (2026-11-26)

Fixes from player reports: Pinstress invisible, Shakra and Garmond slipping into dialogue mid-fight, First Sinner HUD, Savage Beastfly losing aggro, entry points moved off nine bosses, crest slots, bench and item popups.

玩家反馈修复：针姬隐身、沙克拉和加蒙德打到一半变回对话、原初罪者没有血量显示、残暴的兽蝇丢失仇恨、挪开 9 场的入场点、纹章槽、长椅和获得提示。

## 0.10.2 (2026-11-26)

HUD missing in Godhome saves and after Widow; audio in the Hollow Knight Godhome rooms; Fourth Chorus platforms.

神居存档和打完寡妇后 HUD 不显示；前作神居房间的声音；第四圣咏团的浮台。

## 0.10.1 (2026-11-26)

New Godhome saves go to the Hollow Knight Godhome again; Pantheon of Pharloom name; save-guard and return-to-title fixes.

主菜单新建的神居存档重新直接进前作神居；纺络万神殿改名；存档保护和回标题的修复。
