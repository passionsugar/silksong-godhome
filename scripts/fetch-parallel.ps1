# [Godhome] 并行分段下载大文件（这台电脑出境带宽单连接只有 ~16KB/s，但并发能线性叠加）。
# 可重复运行：已完成且校验过的分段会跳过。
# 用法: .\fetch-parallel.ps1 -Url <url> -Out <file> [-Sha512 <hex>] [-Sha256 <hex>] [-ChunkMB 8] [-Parallel 32]
#
# Codex 审计 0.10.3：
# - 分段目录绑定下载源：Out.parts\meta.json 记 URL、总长度、ETag、分段大小和预期 hash，对不上就清掉重下，
#   不会把别的 URL / 版本留下的同样长度的分段拼进来。
# - 每段都核对服务器回的是 206 和对应的 Content-Range（服务器不认 Range 时会回整个文件）。
# - 先合并到临时文件，大小和 hash 都对了才替换 Out。
# - 并发、分段大小、轮数要在合理范围内；分段一直下不完时报出最后一次失败的原因。
param(
  [Parameter(Mandatory)] [string]$Url,
  [Parameter(Mandatory)] [string]$Out,
  [string]$Sha512,
  [string]$Sha256,
  [ValidateRange(0, [int64]::MaxValue)] [int64]$Size = 0,
  [ValidateRange(1, 1024)] [int]$ChunkMB = 8,
  [ValidateRange(1, 256)] [int]$Parallel = 32,
  [ValidateRange(1, 1000)] [int]$MaxRounds = 30
)
$ErrorActionPreference = 'Stop'
$Out = [IO.Path]::GetFullPath($Out)
$partsDir = "$Out.parts"
if (-not $Sha512 -and -not $Sha256) {
  Write-Warning '[Godhome] 没给 -Sha256 / -Sha512：只能核对大小，核对不了内容 / no hash given, only the size is checked'
}

# 文件总大小和 ETag（HEAD 请求跟着跳转，取最后一跳的；最多重试 5 次）。-Size 给了就用它。
$len = $Size
$etag = ''
for ($try = 1; $try -le 5; $try++) {
  $head = curl.exe -s -I -L --max-time 60 $Url
  if ($LASTEXITCODE -ne 0) { continue }
  $hl = $null; $he = ''
  foreach ($line in $head) {
    if ($line -match '^HTTP/') { $hl = $null; $he = '' }
    if ($line -match '^content-length:\s*(\d+)') { $hl = [int64]$Matches[1] }
    if ($line -match '^etag:\s*(.+?)\s*$') { $he = $Matches[1] }
  }
  $etag = $he
  if (-not $len -and $hl) { $len = $hl }
  break
}
if (-not $len) { throw "[Godhome] 拿不到 Content-Length: $Url" }
$len = [int64]$len
$chunk = [int64]$ChunkMB * 1MB
$n = [int][math]::Ceiling($len / $chunk)
Write-Output ("[Godhome] size={0:N0} bytes, {1} chunks x {2} MB, parallel={3}, etag={4}" -f $len, $n, $ChunkMB, $Parallel, $(if ($etag) { $etag } else { '-' }))

# 分段目录绑定下载源。
$meta = [ordered]@{ url = $Url; length = $len; etag = $etag; chunk = $chunk; sha256 = "$Sha256".ToUpper(); sha512 = "$Sha512".ToUpper() }
$metaJson = $meta | ConvertTo-Json -Compress
$metaFile = Join-Path $partsDir 'meta.json'
if (Test-Path -LiteralPath $partsDir) {
  $old = if (Test-Path -LiteralPath $metaFile) { [IO.File]::ReadAllText($metaFile) } else { '' }
  if ($old -ne $metaJson) {
    Write-Output '[Godhome] 已有的分段不是这个下载源的（或没有记录），清掉重下 / existing parts belong to another source, starting over'
    Remove-Item -LiteralPath $partsDir -Recurse -Force
  }
}
New-Item -ItemType Directory -Force -Path $partsDir | Out-Null
[IO.File]::WriteAllText($metaFile, $metaJson)

function Get-Expected([int]$i) { $s = [int64]$i * $chunk; $e = [math]::Min($s + $chunk, $len) - 1; return @($s, $e, ($e - $s + 1)) }
function Get-Part([int]$i) { Join-Path $partsDir ("p{0:D4}" -f $i) }

# 下完的一段：响应头最后一跳必须是 206，Content-Range 和请求的一样，长度也对，才改名成正式分段。
$why = @{}
function Test-Part([int]$i) {
  $p = Get-Part $i
  $tmp = "$p.tmp"; $hdr = "$p.hdr"
  $se = Get-Expected $i
  try {
    if (-not (Test-Path -LiteralPath $tmp)) { $why[$i] = 'curl 没写出文件 / no output'; return }
    $status = ''; $range = ''
    if (Test-Path -LiteralPath $hdr) {
      foreach ($line in [IO.File]::ReadAllLines($hdr)) {
        if ($line -match '^HTTP/\S+\s+(\d+)') { $status = $Matches[1]; $range = '' }
        if ($line -match '^content-range:\s*(.+?)\s*$') { $range = $Matches[1] }
      }
    }
    $want = "bytes $($se[0])-$($se[1])/$len"
    if ($status -ne '206') { $why[$i] = "HTTP $(if ($status) { $status } else { '?' })（要 206 / expected 206）"; return }
    if ($range -ne $want) { $why[$i] = "Content-Range '$range'（要 / expected '$want'）"; return }
    $got = (Get-Item -LiteralPath $tmp).Length
    if ($got -ne $se[2]) { $why[$i] = "长度 / length $got（要 / expected $($se[2])）"; return }
    Move-Item -LiteralPath $tmp -Destination $p -Force
    $why.Remove($i)
  }
  finally {
    Remove-Item -LiteralPath $tmp, $hdr -Force -ErrorAction SilentlyContinue
  }
}

for ($round = 1; $round -le $MaxRounds; $round++) {
  $todo = @()
  for ($i = 0; $i -lt $n; $i++) {
    $p = Get-Part $i
    if (-not (Test-Path -LiteralPath $p) -or (Get-Item -LiteralPath $p).Length -ne (Get-Expected $i)[2]) { $todo += $i }
  }
  if ($todo.Count -eq 0) { break }
  Write-Output ("[Godhome] round {0}: {1} chunks to fetch" -f $round, $todo.Count)
  $running = @()
  foreach ($i in $todo) {
    while (@($running | Where-Object { -not $_.Proc.HasExited }).Count -ge $Parallel) { Start-Sleep -Milliseconds 500 }
    $se = Get-Expected $i
    $p = Get-Part $i
    Remove-Item -LiteralPath $p, "$p.tmp", "$p.hdr" -Force -ErrorAction SilentlyContinue
    $cargs = @('-s', '-S', '-L', '--retry', '3', '--retry-delay', '2', '--speed-time', '60', '--speed-limit', '1024',
      '-r', "$($se[0])-$($se[1])", '-D', "`"$p.hdr`"", '-o', "`"$p.tmp`"", "`"$Url`"")
    $proc = Start-Process -FilePath curl.exe -NoNewWindow -PassThru -ArgumentList $cargs
    $null = $proc.Handle   # 先拿住句柄，不然 PowerShell 5.1 里进程退出后 ExitCode 读不到
    $running += [pscustomobject]@{ I = $i; Proc = $proc }
  }
  # 不用 Wait-Process：几百个短命进程后 PID 可能被复用，Wait-Process 会卡住。
  while (@($running | Where-Object { -not $_.Proc.HasExited }).Count -gt 0) { Start-Sleep -Seconds 1 }
  foreach ($r in $running) {
    Test-Part $r.I
    if ($r.Proc.ExitCode -ne 0) { $why[$r.I] = "curl 退出码 / exit code $($r.Proc.ExitCode)$(if ($why.ContainsKey($r.I)) { '；' + $why[$r.I] })" }
  }
}

# 先确认每段都齐了，再合并到临时文件，核对大小和 hash，都对了再替换 Out。
for ($i = 0; $i -lt $n; $i++) {
  $p = Get-Part $i
  if (-not (Test-Path -LiteralPath $p) -or (Get-Item -LiteralPath $p).Length -ne (Get-Expected $i)[2]) {
    throw "[Godhome] chunk $i incomplete after $MaxRounds rounds: $(if ($why.ContainsKey($i)) { $why[$i] } else { 'unknown' })"
  }
}
$tmpOut = "$Out.tmp"
$fs = [IO.File]::Create($tmpOut)
try {
  for ($i = 0; $i -lt $n; $i++) {
    $b = [IO.File]::ReadAllBytes((Get-Part $i)); $fs.Write($b, 0, $b.Length)
  }
}
finally { $fs.Close() }
try {
  if ((Get-Item -LiteralPath $tmpOut).Length -ne $len) { throw "[Godhome] size mismatch" }
  if ($Sha512) { $h = (Get-FileHash -LiteralPath $tmpOut -Algorithm SHA512).Hash; if ($h -ne $Sha512.ToUpper()) { throw "[Godhome] SHA512 mismatch: $h" }; Write-Output '[Godhome] SHA512 OK' }
  if ($Sha256) { $h = (Get-FileHash -LiteralPath $tmpOut -Algorithm SHA256).Hash; if ($h -ne $Sha256.ToUpper()) { throw "[Godhome] SHA256 mismatch: $h" }; Write-Output '[Godhome] SHA256 OK' }
}
catch {
  Remove-Item -LiteralPath $tmpOut -Force -ErrorAction SilentlyContinue
  throw
}
if (Test-Path -LiteralPath $Out) { [IO.File]::Replace($tmpOut, $Out, $null) } else { [IO.File]::Move($tmpOut, $Out) }
Remove-Item -LiteralPath $partsDir -Recurse -Force
Write-Output "[Godhome] done: $Out"
