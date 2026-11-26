# The Loom of Recollection · 织忆圣所

[简体中文](README.md) | **English**

A boss-revisit and pantheon boss-rush mod for *Hollow Knight: Silksong* (BepInEx 5 plugin). Unofficial fan work, not affiliated with Team Cherry.

## Features

- **A separate save**: on the main menu, pick an empty save slot, then choose "The Loom of Recollection" in the play mode menu. You start with every ability, tool and crest. Your normal saves are not touched: the mod only changes things inside this save.
- **Boss revisits**: 50 boss fights. You enter through a dream gate and come back to the statue afterwards. Three difficulty tiers (Plain Weave, Double Weave, Severed Thread).
- **Pantheons**: five pantheons, four bindings (needle, masks, silk, tools), rest stops along the way and a results screen at the end. The fifth pantheon unlocks after you clear the first four.
- **Hollow Knight's Godhome (optional)**: if *Hollow Knight* is installed on the same PC, the lobby, the workshop, the pantheon doors, the rest-stop spa and the pantheon transition effects and sounds come from the original Godhome scenes. They are read at runtime from your own copy of the game; nothing from either game is included in this repository or the release archives. Without *Hollow Knight*, the mod builds its own sanctum from Silksong's assets.

## Install

**One-click install**: download the archive from [Releases](https://github.com/passionsugar/silksong-godhome/releases), extract all of it, close the game and double-click `install.bat`. It finds the game in your Steam libraries, installs BepInEx and the dependency plugins from Thunderstore if they are missing (plus UnitySceneRepacker when Hollow Knight is installed), then copies the mod to `BepInEx\plugins\Godhome`. It never touches your saves or the game's own files; `uninstall.bat` removes the mod. For a game installed elsewhere, run `install.ps1 -GameDir "<game folder>"`. Details and manual install steps are in the `README.txt` inside the archive.

You need (the installer sets these up):

- *Hollow Knight: Silksong* (tested on game version 1.0.30000)
- BepInEx 5 (`silksong_modding-BepInExPack_Silksong` 1.0.3)
- Dependency plugins (on Thunderstore): `silksong_modding-DataManager` 1.2.2 and `silksong_modding-I18N` 1.1.1
- For the optional Hollow Knight Godhome: `jakobhellermann-UnitySceneRepacker` 0.3.0 and *Hollow Knight* itself. Steam installs are found automatically; otherwise set `Experimental.Hk1GameDir` in `BepInEx\config\local.silksong.godhome.cfg` to the game folder.

## Build from source

- You need the .NET SDK (developed and tested with 10.0; the project uses the latest C# language version, so older SDKs may fail). Game references come from the `Silksong.GameLibs` NuGet package, so the game folder is not needed to build.
- `dotnet build src/Godhome/Godhome.csproj -c Release`
- To install into the game right after building, add `-p:SilksongPluginsFolder=<game folder>\BepInEx\plugins`, or put a `SilksongPath.props` (not tracked by git) in `src/Godhome/`.
- To build the release archive: `powershell -File scripts/package.ps1`. The script's paths are in `scripts/env.ps1`; put anything that differs on your machine in `scripts/env.local.ps1` (not tracked by git).

## Documentation

The documentation is in Chinese.

- `PROGRESS.md`: development log (what was done at each step, how it was tested, code reviews and fixes)
- `docs/design.md`, `docs/v2-design.md`, `docs/pantheon-design.md`: design
- `docs/hk1-port.md`, `docs/hk1-godhome-research.md`: the Hollow Knight Godhome port
- `docs/boss-inventory.md`, `data/bosses.json`: boss list and arena data

## License

MIT, see [`LICENSE`](LICENSE). *Hollow Knight*, *Hollow Knight: Silksong* and their assets belong to Team Cherry; this repository and the release archives contain no game files.
