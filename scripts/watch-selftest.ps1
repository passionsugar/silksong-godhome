# [Godhome] 盯着自检：等到游戏退出、自检结束或超时，然后打印 Godhome 日志和报告。
# 用法: & scripts\watch-selftest.ps1 -Minutes 8
param([double]$Minutes = 8, [switch]$Shot)
. "$PSScriptRoot\env.ps1"
$log = Join-Path $Godhome.Game 'BepInEx\LogOutput.log'
$report = Join-Path $Godhome.Game 'BepInEx\Godhome\selftest-latest.md'
$start = Get-Date
$end = $start.AddMinutes($Minutes)
$seenGame = $false
$reason = 'timeout'
while ((Get-Date) -lt $end) {
    $running = [bool](Get-Process 'Hollow Knight Silksong' -ErrorAction SilentlyContinue)
    if ($running) { $seenGame = $true }
    if ($seenGame -and -not $running) { $reason = 'game exited'; break }
    if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $start -and
        (Select-String -Path $log -Pattern '自检结束|自检中止' -Quiet -Encoding UTF8)) {
        $reason = 'self-test finished'
        Start-Sleep 12
        break
    }
    Start-Sleep 5
}
"== 结束原因 / reason: $reason  ($([int]((Get-Date) - $start).TotalSeconds)s)  game running: $([bool](Get-Process 'Hollow Knight Silksong' -ErrorAction SilentlyContinue))"
"== Godhome / error lines =="
Get-Content $log -Encoding UTF8 | Select-String -Pattern '\[Godhome\]|Exception|\[Error' |
    ForEach-Object { $l = $_.Line; $l.Substring(0, [Math]::Min(300, $l.Length)) } | Select-Object -Last 120
if (Test-Path $report) { "== report ($((Get-Item $report).LastWriteTime)) =="; Get-Content $report -Encoding UTF8 }
if ($Shot -and (Get-Process 'Hollow Knight Silksong' -ErrorAction SilentlyContinue)) {
    & "$PSScriptRoot\mcp.ps1" Screenshot @{ use_annotation = $false } | Select-String 'IMAGE:'
}
