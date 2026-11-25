# 织忆圣所 · The Loom of Recollection

**简体中文** | [English](README.en.md)

《空洞骑士：丝之歌》的 Boss 重温 / 万神殿连战模组（BepInEx 5 插件）。非官方粉丝作品，与 Team Cherry 无关。

## 能做什么

- **单独的神居存档**：主菜单选一个空存档槽 →「选择模式」里选「织忆圣所」。一开始就有全部能力、工具和纹章。普通存档不受影响：模组的改动只在神居存档里生效。
- **重温 Boss**：50 场 Boss 战，从梦境入口进场，打完回到雕像前；三档难度（素织、重织、断丝）。
- **万神殿**：五个万神殿，四种束缚（织针、面具、丝、工具），中途有休息点，打完有结算。第五个万神殿要先通关前四个。
- **前作神居（可选）**：同一台电脑装了《空洞骑士》时，神居存档的大厅、手办屋、万神殿的门、休息点的温泉、连战的转场特效和音效，直接用前作神居的场景。这些场景在运行时从你本机的前作游戏文件里读取，仓库和安装包里不含任何游戏文件。没装前作，就用丝之歌素材搭的圣所。

## 安装

下载 [Releases](https://github.com/passionsugar/silksong-godhome/releases) 里的压缩包，照里面的 `README.txt` 装。需要：

- 正版《空洞骑士：丝之歌》（测试用的版本 1.0.30000）
- BepInEx 5（`silksong_modding-BepInExPack_Silksong` 1.0.3）
- 前置插件（Thunderstore 上有）：`silksong_modding-DataManager` 1.2.2、`silksong_modding-I18N` 1.1.1
- 想用前作神居的，再装：`jakobhellermann-UnitySceneRepacker` 0.3.0，以及《空洞骑士》本体。Steam 版会自动找到；装在别处的，在 `BepInEx\config\local.silksong.godhome.cfg` 的 `Experimental.Hk1GameDir` 里填游戏目录。

## 从源码构建

- 需要 .NET SDK（开发和测试用的是 10.0；项目用最新的 C# 语言版本，旧的 SDK 可能编不过）。游戏的引用来自 NuGet 包 `Silksong.GameLibs`，构建不需要游戏目录。
- `dotnet build src/Godhome/Godhome.csproj -c Release`
- 想构建完直接装进游戏：构建时加 `-p:SilksongPluginsFolder=<游戏目录>\BepInEx\plugins`，或者在 `src/Godhome/` 放一个 `SilksongPath.props`（不进 git）。
- 打安装包：`powershell -File scripts/package.ps1`。脚本的路径在 `scripts/env.ps1`，和默认值不一样的，写进 `scripts/env.local.ps1`（不进 git）。

## 文档

- `PROGRESS.md`：开发日志（每一步做了什么、怎么实测、代码审查和修复）
- `docs/design.md`、`docs/v2-design.md`、`docs/pantheon-design.md`：设计
- `docs/hk1-port.md`、`docs/hk1-godhome-research.md`：前作神居的移植
- `docs/boss-inventory.md`、`data/bosses.json`：Boss 清单和场地数据

## 许可

MIT，见 [`LICENSE`](LICENSE)。《空洞骑士》《空洞骑士：丝之歌》和它们的素材归 Team Cherry 所有；本仓库和安装包不含任何游戏文件。
