# Changelog

Newest first. Release archives and full notes: https://github.com/passionsugar/silksong-godhome/releases

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
