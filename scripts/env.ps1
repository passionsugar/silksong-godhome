# [Godhome] 开发环境：在脚本开头 dot-source 这个文件（. scripts\env.ps1）。
# 下面是按 Steam 默认安装位置写的默认值。本机不一样的（游戏目录、存档目录、.NET / Python 的位置、镜像源），
# 写进同目录的 env.local.ps1（不进 git），它在这里最后被 dot-source，可以直接改 $Godhome 的项、往 PATH 里加东西。
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
$Godhome = @{
  Root        = Split-Path -Parent $PSScriptRoot
  Game        = $(if ($env:SILKSONG_DIR) { $env:SILKSONG_DIR } else { 'C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight Silksong' })
  Saves       = Join-Path $env:USERPROFILE 'AppData\LocalLow\Team Cherry\Hollow Knight Silksong'
  NuGetMirror = ''
  PyPIMirror  = ''
}
$__godhomeLocal = Join-Path $PSScriptRoot 'env.local.ps1'
if (Test-Path $__godhomeLocal) {
  . $__godhomeLocal
}
Remove-Variable __godhomeLocal
# 由游戏目录推出来的（env.local.ps1 改了 Game 也跟着变）。
$Godhome.Managed = Join-Path $Godhome.Game 'Hollow Knight Silksong_Data\Managed'
$Godhome.Bundles = Join-Path $Godhome.Game 'Hollow Knight Silksong_Data\StreamingAssets\aa\StandaloneWindows64'
