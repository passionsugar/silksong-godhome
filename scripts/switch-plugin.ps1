# [Godhome] 在第一版（0.2.11，普通存档里坐长椅进入）和第二版（神居存档）之间切换装着的插件。
# 游戏必须关着。只动 BepInEx\plugins\local-Godhome 这一个目录。
#
# 用法：
#   scripts\switch-plugin.ps1 v1    换成第一版（builds\v1-0.2.11）
#   scripts\switch-plugin.ps1 v2    换回第二版（builds\v2-latest，每次换成 v1 前自动存一份）
#   scripts\switch-plugin.ps1       只显示现在装的是哪一版
param([ValidateSet('v1', 'v2', '')][string]$To = '')

. (Join-Path $PSScriptRoot 'env.ps1')
$plugins = Join-Path $Godhome.Game 'BepInEx\plugins'
$dst = Join-Path $plugins 'local-Godhome'
$v1 = Join-Path $Godhome.Root 'builds\v1-0.2.11\local-Godhome'
$v2 = Join-Path $Godhome.Root 'builds\v2-latest\local-Godhome'

function Get-InstalledVersion {
    $dll = Join-Path $dst 'Godhome.dll'
    if (Test-Path $dll) { (Get-Item $dll).VersionInfo.FileVersion } else { '(没装 / none)' }
}

$now = Get-InstalledVersion
"现在装的 / installed: $now"
if (-not $To) { return }

if (Get-Process 'Hollow Knight Silksong' -ErrorAction SilentlyContinue) {
    throw '游戏还开着，先关掉 / the game is running, close it first'
}

$src = if ($To -eq 'v1') { $v1 } else { $v2 }
if (-not (Test-Path (Join-Path $src 'Godhome.dll'))) {
    throw "找不到要换上的版本 / not found: $src"
}

# 换成第一版之前，把装着的第二版存进 builds\v2-latest，之后能换回来。
if ($To -eq 'v1' -and $now -notlike '0.2.*' -and (Test-Path (Join-Path $dst 'Godhome.dll'))) {
    if (Test-Path $v2) { Remove-Item $v2 -Recurse -Force }
    New-Item -ItemType Directory $v2 -Force | Out-Null
    Copy-Item (Join-Path $dst '*') $v2 -Recurse -Force
    "已存下第二版 / saved v2 ($now) → $v2"
}

if (Test-Path $dst) { Remove-Item $dst -Recurse -Force }
New-Item -ItemType Directory $dst -Force | Out-Null
Copy-Item (Join-Path $src '*') $dst -Recurse -Force

$bad = 0
Get-ChildItem $src -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($src.Length + 1)
    if ((Get-FileHash $_.FullName).Hash -ne (Get-FileHash (Join-Path $dst $rel)).Hash) { $bad++ }
}
"换好了 / switched to ${To}: $(Get-InstalledVersion)（逐个比对哈希，不一致 / mismatches: $bad）"
