# [Godhome] 把运行时依赖插件（DataManager、I18N）从 NuGet 缓存复制到 BepInEx\plugins。
# 只往 BepInEx\plugins 放 dll，不碰游戏原文件。游戏运行时拒绝执行。
param([switch]$WhatIf)
. "$PSScriptRoot\env.ps1"
$ErrorActionPreference = 'Stop'

if (Get-Process -Name 'Hollow Knight Silksong' -ErrorAction SilentlyContinue) {
    throw '[Godhome] 游戏正在运行，先关掉游戏 / the game is running'
}
$plugins = Join-Path $Godhome.Game 'BepInEx\plugins'
if (-not (Test-Path $plugins)) { throw "[Godhome] 没有 BepInEx\plugins：$plugins" }

$deps = @(
    @{ Id = 'silksong.datamanager'; Version = '1.2.2'; Dll = 'Silksong.DataManager.dll'; Folder = 'silksong_modding-DataManager' },
    @{ Id = 'silksong.i18n';        Version = '1.1.1'; Dll = 'Silksong.I18N.dll';        Folder = 'silksong_modding-I18N' }
)
$manifest = @()
foreach ($d in $deps) {
    $src = Join-Path $env:USERPROFILE ".nuget\packages\$($d.Id)\$($d.Version)\lib\netstandard2.1\$($d.Dll)"
    if (-not (Test-Path $src)) { throw "[Godhome] NuGet 缓存里没有 $src（先在 src\Godhome 里 dotnet restore）" }
    $dstDir = Join-Path $plugins $d.Folder
    $dst = Join-Path $dstDir $d.Dll
    $hash = (Get-FileHash $src -Algorithm SHA256).Hash
    if ($WhatIf) { "would copy $src -> $dst ($hash)"; continue }
    New-Item -ItemType Directory -Force $dstDir | Out-Null
    Copy-Item $src $dst -Force
    "installed $($d.Folder)\$($d.Dll) $($d.Version) sha256=$hash"
    $manifest += "BepInEx\plugins\$($d.Folder)\$($d.Dll)  $($d.Version)  sha256=$hash"
}
if (-not $WhatIf) {
    $m = Join-Path $Godhome.Root 'docs\deps-install-manifest.txt'
    $text = "# [Godhome] 依赖插件安装清单（scripts\install-deps.ps1 生成，$(Get-Date -Format 'yyyy-MM-dd HH:mm')）`n# 卸载：删除下面列出的文件夹`n" + ($manifest -join "`n") + "`n"
    [IO.File]::WriteAllText($m, $text, (New-Object Text.UTF8Encoding $false))
    "manifest: $m"
}
