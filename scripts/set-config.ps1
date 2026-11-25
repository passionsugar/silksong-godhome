# [Godhome] 修改 BepInEx\config\local.silksong.godhome.cfg 里的配置项（游戏运行时不要改）。
# 用法: & scripts\set-config.ps1 @{ 'Debug.Enabled' = 'true'; 'Debug.AutoLoadSlot' = '4' }
param([Parameter(Mandatory = $true)][hashtable]$Values)
. "$PSScriptRoot\env.ps1"
if (Get-Process 'Hollow Knight Silksong' -ErrorAction SilentlyContinue) { throw '[Godhome] 游戏正在运行，先关掉再改配置 / close the game first' }
$cfg = Join-Path $Godhome.Game 'BepInEx\config\local.silksong.godhome.cfg'
$lines = [System.Collections.Generic.List[string]]([IO.File]::ReadAllLines($cfg, [Text.Encoding]::UTF8))
foreach ($k in $Values.Keys) {
    $section, $key = $k.Split('.', 2)
    $inSection = $false; $done = $false
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\[(.+)\]\s*$') { $inSection = ($Matches[1] -eq $section); continue }
        if ($inSection -and $lines[$i] -match ('^' + [regex]::Escape($key) + '\s*=')) {
            $lines[$i] = "$key = $($Values[$k])"; $done = $true; break
        }
    }
    if (-not $done) { throw "[Godhome] 配置里没找到 $k" }
}
[IO.File]::WriteAllLines($cfg, $lines, (New-Object Text.UTF8Encoding $false))
$section = ''
foreach ($l in $lines) {
    if ($l -match '^\[(.+)\]') { $section = $Matches[1]; continue }
    if ($l -match '^(\w+)\s*=\s*(.*)$') { "$section.$($Matches[1]) = $($Matches[2])" }
}
