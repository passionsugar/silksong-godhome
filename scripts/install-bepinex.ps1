# [Godhome] 安装 BepInExPack_Silksong 到游戏目录（只新增文件，不覆盖任何游戏原文件）。
# 用法: .\install-bepinex.ps1 -Zip <silksong_modding-BepInExPack_Silksong-x.y.z.zip>
param([Parameter(Mandatory)][string]$Zip)
. "$PSScriptRoot\env.ps1"
$ErrorActionPreference = 'Stop'
$game = $Godhome.Game
if (Get-Process -Name 'Hollow Knight Silksong' -ErrorAction SilentlyContinue) { throw '[Godhome] 游戏正在运行，先关掉游戏' }

$tmp = Join-Path $env:TEMP ('bepinex_' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null
tar.exe -xf $Zip -C $tmp
$pack = Join-Path $tmp 'BepInExPack'
if (-not (Test-Path $pack)) { throw '[Godhome] 压缩包里没有 BepInExPack 目录' }

# 只装 Windows 需要的文件；Linux/Mac 的启动器和 doorstop 库不装
$skip = @('libdoorstop.so', 'libdoorstop.dylib', 'run_bepinex.sh')
$files = Get-ChildItem $pack -Recurse -File -Force | Where-Object { $skip -notcontains $_.Name }

# 先检查冲突：游戏目录里已有同名文件就停下（规则：不改游戏原文件）
$conflicts = foreach ($f in $files) {
  $rel = $f.FullName.Substring($pack.Length + 1)
  $dst = Join-Path $game $rel
  if (Test-Path -LiteralPath $dst) { $rel }
}
if ($conflicts) { throw "[Godhome] 这些文件已存在，不覆盖: $($conflicts -join ', ')" }

$manifest = @("# [Godhome] BepInEx 安装清单（卸载时按这个列表删除）", "zip: $Zip", "sha256: $((Get-FileHash $Zip).Hash)", "installed: $(Get-Date -Format o)", "game: $game", "")
foreach ($f in $files) {
  $rel = $f.FullName.Substring($pack.Length + 1)
  $dst = Join-Path $game $rel
  New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
  Copy-Item -LiteralPath $f.FullName -Destination $dst
  $manifest += $rel
}
New-Item -ItemType Directory -Force (Join-Path $game 'BepInEx\plugins') | Out-Null
[IO.File]::WriteAllLines((Join-Path $Godhome.Root 'docs\bepinex-install-manifest.txt'), $manifest, (New-Object Text.UTF8Encoding $false))
Remove-Item -Recurse -Force $tmp
"[Godhome] 安装了 $($files.Count) 个文件"
Get-ChildItem -LiteralPath $game -Force | Select-Object Mode, Name | Format-Table -AutoSize | Out-String
