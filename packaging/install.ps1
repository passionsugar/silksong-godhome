# 织忆圣所 The Loom of Recollection {VERSION} — 一键安装 / one-click installer
#
# 双击同一个文件夹里的 install.bat 就行（它用 PowerShell 跑这个脚本）。卸载双击 uninstall.bat。
# 做的事（只做这些）：
#   1. 找到《空洞骑士：丝之歌》的游戏文件夹（Steam 自动找；找不到会让你粘贴路径；也可以用 -GameDir 指定）。
#   2. 没装 BepInEx 5 的，从 Thunderstore 下载 BepInExPack_Silksong 装进游戏文件夹（只加 BepInEx 自己的文件，不改游戏原文件）。
#   3. 缺前置插件 DataManager、I18N 的，从 Thunderstore 下载，放进 BepInEx\plugins。
#      本机装了《空洞骑士》（前作）的，再装 UnitySceneRepacker（前作神居要用；不想要加 -NoRepacker）。
#   4. 把这个压缩包里的 BepInEx\plugins\Godhome 复制进游戏（旧版本整个换掉）。
# 不碰存档，不改游戏原文件，不改配置文件。游戏开着时不装。
#
# 参数：
#   -GameDir <路径>   游戏文件夹（有 Hollow Knight Silksong.exe 的那个）
#   -Uninstall        卸载：只删 BepInEx\plugins\Godhome（前置插件和 BepInEx 留着，别的模组可能也要用）
#   -NoRepacker       不装 UnitySceneRepacker
#   -NoDownload       不联网：缺什么就停下来告诉你
#   -PackageDir <路径> 前置插件的 zip 已经下好放在这里（文件名 作者-名字-版本.zip，和 Thunderstore 下载的一样）
#   -Yes              不问，直接装
param(
    [string]$GameDir,
    [switch]$Uninstall,
    [switch]$NoRepacker,
    [switch]$NoDownload,
    [string]$PackageDir,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$ModVersion = '{VERSION}'
$ExeName = 'Hollow Knight Silksong.exe'
$ProcName = 'Hollow Knight Silksong'
$SteamFolder = 'Hollow Knight Silksong'

# 要装的前置（Thunderstore 上的作者、名字、版本；Dll 用来认出已经装了的）。
$BepInExPack = @{ Ns = 'silksong_modding'; Name = 'BepInExPack_Silksong'; Version = '1.0.3' }
$Deps = @(
    @{ Ns = 'silksong_modding'; Name = 'DataManager'; Version = '1.2.2'; Dll = 'Silksong.DataManager.dll' },
    @{ Ns = 'silksong_modding'; Name = 'I18N'; Version = '1.1.1'; Dll = 'Silksong.I18N.dll' }
)
$Repacker = @{ Ns = 'jakobhellermann'; Name = 'UnitySceneRepacker'; Version = '0.3.0'; Dll = 'UnitySceneRepacker.Plugin.dll' }

function Say([string]$zh, [string]$en) { Write-Host $zh; if ($en) { Write-Host "  $en" -ForegroundColor DarkGray } }
function Step([string]$text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Ok([string]$text) { Write-Host "  [OK] $text" -ForegroundColor Green }
function Warn([string]$text) { Write-Host "  [!] $text" -ForegroundColor Yellow }
function Fail([string]$zh, [string]$en) { throw "$zh`n  $en" }

function Ask([string]$question) {
    if ($Yes) { return $true }
    $a = Read-Host "$question [Y/n]"
    return -not ($a -match '^\s*[nN]')
}

# 删别人的文件这种事要玩家亲口说“是”：-Yes 时也不删，默认是“否”。
function AskToDelete([string]$question) {
    if ($Yes) { return $false }
    $a = Read-Host "$question [y/N]"
    return $a -match '^\s*[yY]'
}

# ───────────────────────────── 找游戏 ─────────────────────────────

function Test-GameDir([string]$dir) {
    return $dir -and (Test-Path -LiteralPath (Join-Path $dir $ExeName))
}

function Get-SteamLibraries {
    $roots = @()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam') {
        try {
            $p = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($v in $p.SteamPath, $p.InstallPath) { if ($v) { $roots += ($v -replace '/', '\') } }
        } catch { }
    }
    foreach ($d in [IO.DriveInfo]::GetDrives()) {
        if ($d.DriveType -ne 'Fixed' -or -not $d.IsReady) { continue }
        foreach ($sub in 'SteamLibrary', 'Steam', 'Program Files (x86)\Steam', 'Program Files\Steam', 'Games\Steam') {
            $roots += Join-Path $d.RootDirectory.FullName $sub
        }
    }
    $libs = @()
    foreach ($r in $roots | Select-Object -Unique) {
        if (-not (Test-Path -LiteralPath (Join-Path $r 'steamapps'))) { continue }
        $libs += $r
        $vdf = Join-Path $r 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) {
                $libs += $m.Groups[1].Value.Replace('\\', '\')
            }
        }
    }
    return $libs | Select-Object -Unique
}

function Find-Game {
    if ($GameDir) {
        $g = $GameDir.Trim().Trim('"')
        if ((Test-Path -LiteralPath $g -PathType Leaf) -and (Split-Path $g -Leaf) -eq $ExeName) { $g = Split-Path $g }
        if (Test-GameDir $g) { return (Resolve-Path -LiteralPath $g).Path }
        Fail "-GameDir 指的文件夹里没有 $ExeName：$g" "-GameDir does not contain $ExeName"
    }
    # 压缩包直接解压在游戏文件夹里的情况。
    if (Test-GameDir $PSScriptRoot) { return $PSScriptRoot }
    foreach ($lib in Get-SteamLibraries) {
        $g = Join-Path $lib "steamapps\common\$SteamFolder"
        if (Test-GameDir $g) { return $g }
    }
    if ($Yes) { Fail '没找到游戏文件夹，用 -GameDir 指定' 'game folder not found, pass -GameDir' }
    Say '没自动找到游戏。请把游戏文件夹的路径粘贴到下面（Steam 里右键游戏 → 管理 → 浏览本地文件，复制地址栏）：' `
        'Could not find the game. Paste the game folder path (Steam: right click the game → Manage → Browse local files):'
    for ($i = 0; $i -lt 3; $i++) {
        $g = (Read-Host '游戏文件夹 / game folder').Trim().Trim('"')
        if ((Test-Path -LiteralPath $g -PathType Leaf) -and (Split-Path $g -Leaf) -eq $ExeName) { $g = Split-Path $g }
        if (Test-GameDir $g) { return (Resolve-Path -LiteralPath $g).Path }
        Warn "这里没有 $ExeName，再试一次 / $ExeName not found there"
    }
    Fail '没有可用的游戏文件夹，什么都没改。' 'No game folder, nothing changed.'
}

function Find-Hk1 {
    foreach ($lib in Get-SteamLibraries) {
        $g = Join-Path $lib 'steamapps\common\Hollow Knight'
        if (Test-Path -LiteralPath (Join-Path $g 'hollow_knight_Data\globalgamemanagers')) { return $g }
    }
    return $null
}

# ───────────────────────────── 下载和解包 ─────────────────────────────

$Work = Join-Path ([IO.Path]::GetTempPath()) ('godhome-install-' + [guid]::NewGuid().ToString('N'))

function Get-PackageZip($pkg) {
    $file = "$($pkg.Ns)-$($pkg.Name)-$($pkg.Version).zip"
    if ($PackageDir) {
        $p = Join-Path $PackageDir $file
        if (Test-Path -LiteralPath $p) { Ok "用本地的 / using local $file"; return $p }
    }
    $url = "https://thunderstore.io/package/download/$($pkg.Ns)/$($pkg.Name)/$($pkg.Version)/"
    if ($NoDownload) { Fail "缺 $file（加了 -NoDownload，不联网）。去 $url 下载，放进 -PackageDir 指的文件夹。" "missing $file (-NoDownload)" }
    New-Item -ItemType Directory -Force $Work | Out-Null
    $out = Join-Path $Work $file
    Write-Host "  下载 / downloading $url"
    try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }
    $last = $null
    for ($try = 1; $try -le 3; $try++) {
        try {
            Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing -TimeoutSec 600
            $last = $null
            break
        } catch {
            $last = $_.Exception.Message
            Warn "第 $try 次下载失败 / download attempt $try failed: $last"
            Start-Sleep -Seconds (2 * $try)
        }
    }
    if ($last) { Fail "下载失败：$url（$last）。可以手动下载后用 -PackageDir 指定文件夹。" "download failed" }
    return $out
}

# 解包到临时文件夹，并核对 Thunderstore 的 manifest.json 是不是要的那个包、那个版本。
function Expand-Package($pkg, [string]$zip) {
    $dir = Join-Path $Work ("$($pkg.Name)-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force $dir | Out-Null
    $za = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $root = [IO.Path]::GetFullPath($dir + '\')
        foreach ($e in $za.Entries) {
            $dst = [IO.Path]::GetFullPath((Join-Path $dir ($e.FullName -replace '/', '\')))
            if (-not $dst.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { Fail "压缩包里有不安全的路径：$($e.FullName)" "unsafe path in zip" }
            if ($e.FullName.EndsWith('/') -or $e.FullName.EndsWith('\')) { New-Item -ItemType Directory -Force $dst | Out-Null; continue }
            New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
        }
    } finally { $za.Dispose() }
    $mf = Join-Path $dir 'manifest.json'
    if (Test-Path -LiteralPath $mf) {
        $m = [IO.File]::ReadAllText($mf, [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ($m.name -ne $pkg.Name -or $m.version_number -ne $pkg.Version) {
            Fail "下载到的包不对：要 $($pkg.Name) $($pkg.Version)，拿到 $($m.name) $($m.version_number)" "unexpected package"
        }
    } else {
        Fail "$zip 里没有 manifest.json，不像 Thunderstore 的包" "no manifest.json in the package"
    }
    return $dir
}

# 把 from 里的文件复制到 to（保持子文件夹）。-NoOverwrite 时已经有的文件不动，返回跳过的列表。
function Copy-Tree([string]$from, [string]$to, [switch]$NoOverwrite) {
    $skipped = @()
    $base = (Resolve-Path -LiteralPath $from).Path.TrimEnd('\')
    foreach ($f in Get-ChildItem -LiteralPath $from -Recurse -File -Force) {
        $rel = $f.FullName.Substring($base.Length + 1)
        $dst = Join-Path $to $rel
        if ($NoOverwrite -and (Test-Path -LiteralPath $dst)) { $skipped += $rel; continue }
        New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
        Copy-Item -LiteralPath $f.FullName -Destination $dst -Force
    }
    return , $skipped
}

# ───────────────────────────── 已经装了什么 ─────────────────────────────

function Get-DllVersion([IO.FileInfo]$dll) {
    # 同一个文件夹里有 Thunderstore 的 manifest.json（模组管理器、这个脚本装的）就信它；没有就看文件版本。
    $mf = Join-Path $dll.DirectoryName 'manifest.json'
    if (Test-Path -LiteralPath $mf) {
        try { return [version]((Get-Content -LiteralPath $mf -Raw -Encoding UTF8 | ConvertFrom-Json).version_number) } catch { }
    }
    foreach ($v in $dll.VersionInfo.ProductVersion, $dll.VersionInfo.FileVersion) {
        if ($v -match '^(\d+\.\d+\.\d+)') { try { return [version]$Matches[1] } catch { } }
    }
    return $null
}

function Find-Installed([string]$plugins, $pkg) {
    if (-not (Test-Path -LiteralPath $plugins)) { return @() }
    return @(Get-ChildItem -LiteralPath $plugins -Recurse -File -Filter $pkg.Dll -ErrorAction SilentlyContinue)
}

# ───────────────────────────── 安装 / 卸载 ─────────────────────────────

function Install-BepInEx([string]$game) {
    Step 'BepInEx 5'
    $core = Join-Path $game 'BepInEx\core\BepInEx.dll'
    $loader = Join-Path $game 'winhttp.dll'
    if ((Test-Path -LiteralPath $core) -and (Test-Path -LiteralPath $loader)) {
        $v = (Get-Item -LiteralPath $core).VersionInfo.FileVersion
        if ($v -and -not $v.StartsWith('5.')) { Fail "游戏里装的是 BepInEx $v，这个模组要 BepInEx 5。" "BepInEx $v found, this mod needs BepInEx 5." }
        Ok "已经装了 / already installed (BepInEx $v)"
        return
    }
    $zip = Get-PackageZip $BepInExPack
    $dir = Expand-Package $BepInExPack $zip
    # 包里放游戏文件夹内容的那一层：有 winhttp.dll 和 BepInEx 文件夹的那个。
    $src = Get-ChildItem -LiteralPath $dir -Recurse -Filter 'winhttp.dll' -File | Select-Object -First 1 | ForEach-Object { $_.DirectoryName }
    if (-not $src -or -not (Test-Path -LiteralPath (Join-Path $src 'BepInEx'))) { Fail 'BepInExPack 的结构和预想的不一样，没装。' 'unexpected BepInExPack layout, nothing installed' }
    # 只加文件：已经有的不覆盖（例如别的工具留下的 winhttp.dll），并列出来。
    $skipped = Copy-Tree $src $game -NoOverwrite
    foreach ($s in $skipped) { Warn "已经有了，没覆盖 / kept existing: $s" }
    if (-not (Test-Path -LiteralPath $core)) { Fail 'BepInEx 没装上（BepInEx\core\BepInEx.dll 不在）。' 'BepInEx install failed' }
    Ok "装好了 / installed BepInExPack_Silksong $($BepInExPack.Version)（第一次启动游戏会慢一点 / the first launch takes longer）"
}

function Install-Plugin([string]$game, $pkg) {
    Step "$($pkg.Name) $($pkg.Version)"
    $plugins = Join-Path $game 'BepInEx\plugins'
    $mine = Join-Path $plugins "$($pkg.Ns)-$($pkg.Name)"
    $found = Find-Installed $plugins $pkg
    $need = [version]$pkg.Version
    foreach ($f in $found) {
        $v = Get-DllVersion $f
        $rel = $f.FullName.Substring($game.Length).TrimStart('\')
        if ($v -and $v -ge $need) { Ok "已经装了 / already installed: $rel ($v)"; return }
        if (-not $f.DirectoryName.Equals($mine, [StringComparison]::OrdinalIgnoreCase)) {
            Fail "$rel 是旧版本（$v），要 $need。请先删掉它所在的文件夹（或用模组管理器更新），再运行安装。" "$rel is too old ($v), need $need; remove it first"
        }
    }
    $zip = Get-PackageZip $pkg
    $dir = Expand-Package $pkg $zip
    if (Test-Path -LiteralPath $mine) { Remove-Item -LiteralPath $mine -Recurse -Force }
    New-Item -ItemType Directory -Force $mine | Out-Null
    # Thunderstore 包的常见结构：plugins\ 里是插件文件，patchers\ 是预加载补丁，BepInEx\ 是整个合并；都没有就是根目录里的 dll。
    $copied = $false
    if (Test-Path -LiteralPath (Join-Path $dir 'BepInEx')) {
        $sub = Join-Path $dir 'BepInEx\plugins'
        if (Test-Path -LiteralPath $sub) { [void](Copy-Tree $sub $mine); $copied = $true }
        $sub = Join-Path $dir 'BepInEx\patchers'
        if (Test-Path -LiteralPath $sub) { [void](Copy-Tree $sub (Join-Path $game "BepInEx\patchers\$($pkg.Ns)-$($pkg.Name)")); $copied = $true }
    }
    if (Test-Path -LiteralPath (Join-Path $dir 'plugins')) { [void](Copy-Tree (Join-Path $dir 'plugins') $mine); $copied = $true }
    if (Test-Path -LiteralPath (Join-Path $dir 'patchers')) { [void](Copy-Tree (Join-Path $dir 'patchers') (Join-Path $game "BepInEx\patchers\$($pkg.Ns)-$($pkg.Name)")); $copied = $true }
    if (-not $copied) {
        foreach ($f in Get-ChildItem -LiteralPath $dir -File -Filter *.dll) { Copy-Item -LiteralPath $f.FullName -Destination $mine -Force; $copied = $true }
    }
    if (-not $copied) { Fail "$($pkg.Name) 的包里没找到插件文件。" "no plugin files in the package" }
    # 留一份 manifest.json，下次运行认得出版本（和模组管理器的做法一样）。
    Copy-Item -LiteralPath (Join-Path $dir 'manifest.json') -Destination $mine -Force
    if (-not (Find-Installed $plugins $pkg)) { Fail "$($pkg.Name) 没装上（找不到 $($pkg.Dll)）。" "install failed" }
    Ok "装好了 / installed: BepInEx\plugins\$($pkg.Ns)-$($pkg.Name)"
}

function Install-Godhome([string]$game) {
    Step "织忆圣所 / The Loom of Recollection $ModVersion"
    $src = Join-Path $PSScriptRoot 'BepInEx\plugins\Godhome'
    $plugins = Join-Path $game 'BepInEx\plugins'
    $dst = Join-Path $plugins 'Godhome'
    # 别的地方还有一份 Godhome.dll（以前手动装在别的文件夹、模组管理器装的）会和这份冲突。
    $others = @(Get-ChildItem -LiteralPath $plugins -Recurse -File -Filter 'Godhome.dll' -ErrorAction SilentlyContinue |
        Where-Object { -not $_.DirectoryName.Equals($dst, [StringComparison]::OrdinalIgnoreCase) })
    foreach ($o in $others) {
        $rel = $o.DirectoryName.Substring($game.Length).TrimStart('\')
        Warn "别处还有一份织忆圣所 / another copy: $rel"
        if (AskToDelete "  删掉这份旧的（$rel）？两份同时在会冲突 / delete it?") {
            Remove-Item -LiteralPath $o.DirectoryName -Recurse -Force
            Ok "删掉了 / removed $rel"
        } else {
            Warn '留着了；两份同时在会冲突，请自己删掉旧的那份 / kept; please remove the old copy yourself'
        }
    }
    if ([IO.Path]::GetFullPath($src).TrimEnd('\') -ieq [IO.Path]::GetFullPath($dst).TrimEnd('\')) {
        # 压缩包直接解压在游戏文件夹里：文件已经在位置上了。
        Ok "已经在游戏文件夹里了 / already in place: BepInEx\plugins\Godhome"
        return
    }
    if (Test-Path -LiteralPath $dst) { Remove-Item -LiteralPath $dst -Recurse -Force }
    [void](Copy-Tree $src $dst)
    $a = (Get-FileHash -LiteralPath (Join-Path $src 'Godhome.dll') -Algorithm SHA256).Hash
    $b = (Get-FileHash -LiteralPath (Join-Path $dst 'Godhome.dll') -Algorithm SHA256).Hash
    if ($a -ne $b) { Fail 'Godhome.dll 复制以后不一样，安装失败。' 'copy verification failed' }
    Ok "装好了 / installed: BepInEx\plugins\Godhome"
}

# ───────────────────────────── 主流程 ─────────────────────────────

$code = 0
try {
    Write-Host "织忆圣所 The Loom of Recollection $ModVersion — $(if ($Uninstall) { '卸载 / uninstall' } else { '一键安装 / installer' })" -ForegroundColor Cyan
    if (-not $Uninstall -and -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'BepInEx\plugins\Godhome\Godhome.dll'))) {
        Fail '这个文件夹里没有 BepInEx\plugins\Godhome\Godhome.dll。请先把整个压缩包解压出来，再运行 install.bat。' `
            'BepInEx\plugins\Godhome\Godhome.dll is missing next to this script. Extract the whole zip first.'
    }
    $game = Find-Game
    Say "游戏文件夹 / game folder: $game"
    if (Get-Process -Name $ProcName -ErrorAction SilentlyContinue) {
        Fail '游戏正开着，先关掉游戏再运行。' 'The game is running. Close it first.'
    }

    if ($Uninstall) {
        $dst = Join-Path $game 'BepInEx\plugins\Godhome'
        if (-not (Test-Path -LiteralPath $dst)) { Say '没装织忆圣所，不用卸载。' 'Not installed.' }
        elseif (Ask "删掉 $dst ？存档不受影响 / delete it? saves are not touched") {
            Remove-Item -LiteralPath $dst -Recurse -Force
            Ok '卸载了 / uninstalled'
            Say '前置插件（DataManager、I18N、UnitySceneRepacker）和 BepInEx 留着了，别的模组可能也在用；不要了可以删 BepInEx\plugins 里对应的文件夹。神居存档还在原来的槽里，不要了在游戏里删。' `
                'Dependencies and BepInEx were kept. The Godhome save stays in its slot; delete it in game if you like.'
        } else { Say '取消了，什么都没改。' 'Cancelled, nothing changed.' }
    } else {
        $hk1 = $null
        if (-not $NoRepacker) { $hk1 = Find-Hk1 }
        $plan = @('BepInEx 5（没装的话 / if missing）', 'DataManager 1.2.2、I18N 1.1.1（缺的话 / if missing）')
        if ($hk1) { $plan += "UnitySceneRepacker 0.3.0（找到前作 / Hollow Knight found: $hk1）" }
        $plan += "织忆圣所 / The Loom of Recollection $ModVersion"
        Say '要装 / will install:' ''
        foreach ($p in $plan) { Write-Host "  - $p" }
        if (-not $NoRepacker -and -not $hk1) {
            Say '  （没找到前作《空洞骑士》，不装 UnitySceneRepacker；圣所用丝之歌的素材。）' '  (Hollow Knight not found: the sanctum uses Silksong assets.)'
        }
        if (-not (Ask '开始安装？/ install now?')) { Say '取消了，什么都没改。' 'Cancelled, nothing changed.'; return }

        Install-BepInEx $game
        foreach ($d in $Deps) { Install-Plugin $game $d }
        if ($hk1) { Install-Plugin $game $Repacker }
        Install-Godhome $game

        Write-Host ''
        Write-Host '全部装好了。/ All done.' -ForegroundColor Green
        Say '启动游戏 → 主菜单选一个空的存档槽 → “选择模式”里选「织忆圣所」。' 'Start the game → pick an empty save slot → choose "The Loom of Recollection".'
    }
}
catch {
    Write-Host ''
    Write-Host "出错了 / failed: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host '已经装好的部分不会造成问题；解决后重新运行就行。/ Fix the problem and run it again.' -ForegroundColor Red
    $code = 1
}
finally {
    if (Test-Path -LiteralPath $Work) { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $code
