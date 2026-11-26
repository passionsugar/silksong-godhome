# [Godhome] 把运行时依赖插件（DataManager、I18N）从 NuGet 缓存复制到 BepInEx\plugins。
# 只往 BepInEx\plugins 放 dll，不碰游戏原文件。游戏运行时拒绝执行。
# 两个包都由 src\Godhome 的 dotnet restore 下载（DataManager 是包引用，I18N 是 PackageDownload）。
# 先确认每个来源都在，再开始复制（Codex 审计 0.10.3 F02：不留半装的状态）。
# 玩家装模组用发布包里的一键安装 install.bat（packaging\install.ps1，从 Thunderstore 下载前置），不用这个。
param([switch]$WhatIf)
. "$PSScriptRoot\env.ps1"
$ErrorActionPreference = 'Stop'

if (Get-Process -Name 'Hollow Knight Silksong' -ErrorAction SilentlyContinue) {
    throw '[Godhome] 游戏正在运行，先关掉游戏 / the game is running'
}
$plugins = Join-Path $Godhome.Game 'BepInEx\plugins'
if (-not (Test-Path $plugins)) { throw "[Godhome] 没有 BepInEx\plugins：$plugins" }

# NuGet 缓存的位置照 NuGet 自己的规则问（NUGET_PACKAGES、NuGet.Config 里的 globalPackagesFolder 都算），
# 问不到才用默认的 %USERPROFILE%\.nuget\packages。
$cache = $null
try {
    $line = dotnet nuget locals global-packages --list 2>$null | Select-Object -First 1
    if ($LASTEXITCODE -eq 0 -and $line -match '^[^:]+:\s*(.+)$') { $cache = $Matches[1].Trim() }
} catch { }
if (-not $cache) { $cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' } }
"NuGet 缓存 / global packages: $cache"

$deps = @(
    @{ Id = 'silksong.datamanager'; Version = '1.2.2'; Dll = 'Silksong.DataManager.dll'; Folder = 'silksong_modding-DataManager' },
    @{ Id = 'silksong.i18n';        Version = '1.1.1'; Dll = 'Silksong.I18N.dll';        Folder = 'silksong_modding-I18N' }
)
$missing = @()
foreach ($d in $deps) {
    $d.Src = Join-Path $cache "$($d.Id)\$($d.Version)\lib\netstandard2.1\$($d.Dll)"
    if (-not (Test-Path $d.Src)) { $missing += $d.Src }
}
if ($missing.Count -gt 0) {
    throw "[Godhome] NuGet 缓存里缺这些文件，一个都没复制（先在 src\Godhome 里 dotnet restore）/ missing, nothing copied:`n  " + ($missing -join "`n  ")
}

$manifest = @()
foreach ($d in $deps) {
    $dstDir = Join-Path $plugins $d.Folder
    $dst = Join-Path $dstDir $d.Dll
    $hash = (Get-FileHash $d.Src -Algorithm SHA256).Hash
    if ($WhatIf) { "would copy $($d.Src) -> $dst ($hash)"; continue }
    New-Item -ItemType Directory -Force $dstDir | Out-Null
    Copy-Item $d.Src $dst -Force
    "installed $($d.Folder)\$($d.Dll) $($d.Version) sha256=$hash"
    $manifest += "BepInEx\plugins\$($d.Folder)\$($d.Dll)  $($d.Version)  sha256=$hash"
}
if (-not $WhatIf) {
    $m = Join-Path $Godhome.Root 'docs\deps-install-manifest.txt'
    $text = "# [Godhome] 依赖插件安装清单（scripts\install-deps.ps1 生成，$(Get-Date -Format 'yyyy-MM-dd HH:mm')）`n# 卸载：删除下面列出的文件夹`n" + ($manifest -join "`n") + "`n"
    [IO.File]::WriteAllText($m, $text, (New-Object Text.UTF8Encoding $false))
    "manifest: $m"
}
