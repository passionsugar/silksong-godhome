# [Godhome] 盯着自检：等到游戏退出、自检结束或超时，然后打印 Godhome 日志和报告。
# 用法: & scripts\watch-selftest.ps1 -Minutes 8 [-Shot]
# 只看这次开始盯以后写进日志的内容（Codex 审计 0.10.3：以前搜整个日志，同一进程跑第二轮时会马上撞上第一轮的“自检结束”）。
# 游戏重启时 BepInEx 会重写 LogOutput.log：文件变短了就从头读。
param([double]$Minutes = 8, [switch]$Shot)
. "$PSScriptRoot\env.ps1"
$log = Join-Path $Godhome.Game 'BepInEx\LogOutput.log'
$report = Join-Path $Godhome.Game 'BepInEx\Godhome\selftest-latest.md'

# -Shot 要用的截图脚本只在开发机上有（公开仓库里没有），先检查，没有就提前说清楚、不截图。
$mcp = Join-Path $PSScriptRoot 'mcp.ps1'
if ($Shot -and -not (Test-Path -LiteralPath $mcp)) {
    Write-Warning "[Godhome] 没有 $mcp，-Shot 不可用，不截图 / -Shot needs scripts\mcp.ps1, which is not in this checkout"
    $Shot = $false
}

function Get-LogLength {
    if (Test-Path -LiteralPath $log) { return (Get-Item -LiteralPath $log).Length }
    return 0
}

# 从字节偏移读到文件末尾（游戏开着日志，要允许别人写）。
function Read-LogFrom([int64]$offset) {
    if (-not (Test-Path -LiteralPath $log)) { return '' }
    $fs = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        if ($offset -gt $fs.Length) { $offset = 0 }
        [void]$fs.Seek($offset, [IO.SeekOrigin]::Begin)
        $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)
        return $sr.ReadToEnd()
    }
    finally { $fs.Dispose() }
}

$start = Get-Date
$offset = Get-LogLength
$end = $start.AddMinutes($Minutes)
$seenGame = $false
$reason = 'timeout'
while ((Get-Date) -lt $end) {
    $running = [bool](Get-Process 'Hollow Knight Silksong' -ErrorAction SilentlyContinue)
    if ($running) { $seenGame = $true }
    if ($seenGame -and -not $running) { $reason = 'game exited'; break }
    # 日志变短 = 游戏重启、文件重写了：从头读。
    if ((Get-LogLength) -lt $offset) { $offset = 0 }
    if ((Read-LogFrom $offset) -match '自检结束|自检中止') {
        $reason = 'self-test finished'
        Start-Sleep 12
        break
    }
    Start-Sleep 5
}
"== 结束原因 / reason: $reason  ($([int]((Get-Date) - $start).TotalSeconds)s)  game running: $([bool](Get-Process 'Hollow Knight Silksong' -ErrorAction SilentlyContinue))"
"== Godhome / error lines（这次开始以后 / since this watch started）=="
if ((Get-LogLength) -lt $offset) { $offset = 0 }
(Read-LogFrom $offset) -split "`r?`n" | Where-Object { $_ -match '\[Godhome\]|Exception|\[Error' } |
    ForEach-Object { $_.Substring(0, [Math]::Min(300, $_.Length)) } | Select-Object -Last 120
if (Test-Path -LiteralPath $report) {
    $rt = (Get-Item -LiteralPath $report).LastWriteTime
    if ($rt -ge $start) { "== report ($rt) =="; Get-Content -LiteralPath $report -Encoding UTF8 }
    else { "== 报告不是这次写的（$rt），不打印 / report is older than this watch" }
}
if ($Shot -and (Get-Process 'Hollow Knight Silksong' -ErrorAction SilentlyContinue)) {
    & $mcp Screenshot @{ use_annotation = $false } | Select-String 'IMAGE:'
}
