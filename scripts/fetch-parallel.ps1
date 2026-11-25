# [Godhome] 并行分段下载大文件（这台电脑出境带宽单连接只有 ~16KB/s，但并发能线性叠加）。
# 可重复运行：已完成且大小正确的分段会跳过。
# 用法: .\fetch-parallel.ps1 -Url <url> -Out <file> [-Sha512 <hex>] [-ChunkMB 8] [-Parallel 32]
param(
  [Parameter(Mandatory)] [string]$Url,
  [Parameter(Mandatory)] [string]$Out,
  [string]$Sha512,
  [string]$Sha256,
  [int64]$Size = 0,
  [int]$ChunkMB = 8,
  [int]$Parallel = 32,
  [int]$MaxRounds = 30
)
$ErrorActionPreference = 'Stop'
$partsDir = "$Out.parts"
New-Item -ItemType Directory -Force -Path $partsDir | Out-Null

# 文件总大小（可用 -Size 直接给出；否则 HEAD 请求，最多重试 5 次）
$len = $Size
for ($try = 1; -not $len -and $try -le 5; $try++) {
  $head = curl.exe -s -I -L --max-time 60 $Url
  foreach ($line in $head) { if ($line -match '^content-length:\s*(\d+)') { $len = [int64]$Matches[1] } }
}
if (-not $len) { throw "[Godhome] 拿不到 Content-Length: $Url" }
$len = [int64]$len
$chunk = [int64]$ChunkMB * 1MB
$n = [int][math]::Ceiling($len / $chunk)
Write-Output ("[Godhome] size={0:N0} bytes, {1} chunks x {2} MB, parallel={3}" -f $len, $n, $ChunkMB, $Parallel)

function Get-Expected([int]$i) { $s = [int64]$i * $chunk; $e = [math]::Min($s + $chunk, $len) - 1; return @($s, $e, ($e - $s + 1)) }

for ($round = 1; $round -le $MaxRounds; $round++) {
  $todo = @()
  for ($i = 0; $i -lt $n; $i++) {
    $p = Join-Path $partsDir ("p{0:D4}" -f $i)
    $exp = (Get-Expected $i)[2]
    if (-not (Test-Path $p) -or (Get-Item $p).Length -ne $exp) { $todo += $i }
  }
  if ($todo.Count -eq 0) { break }
  Write-Output ("[Godhome] round {0}: {1} chunks to fetch" -f $round, $todo.Count)
  $running = @()
  foreach ($i in $todo) {
    while (@($running | Where-Object { -not $_.HasExited }).Count -ge $Parallel) { Start-Sleep -Milliseconds 500 }
    $se = Get-Expected $i
    $p = Join-Path $partsDir ("p{0:D4}" -f $i)
    $running += Start-Process -FilePath curl.exe -NoNewWindow -PassThru -ArgumentList @('-s','-L','--retry','3','--retry-delay','2','--speed-time','60','--speed-limit','1024','-r',"$($se[0])-$($se[1])",'-o',"`"$p`"",$Url)
  }
  # 不用 Wait-Process：几百个短命进程后 PID 可能被复用，Wait-Process 会卡住。
  while (@($running | Where-Object { -not $_.HasExited }).Count -gt 0) { Start-Sleep -Seconds 1 }
}

# 合并 + 校验
$fs = [IO.File]::Create($Out)
try {
  for ($i = 0; $i -lt $n; $i++) {
    $p = Join-Path $partsDir ("p{0:D4}" -f $i)
    if (-not (Test-Path $p) -or (Get-Item $p).Length -ne (Get-Expected $i)[2]) { throw "[Godhome] chunk $i incomplete after $MaxRounds rounds" }
    $b = [IO.File]::ReadAllBytes($p); $fs.Write($b, 0, $b.Length)
  }
} finally { $fs.Close() }
if ((Get-Item $Out).Length -ne $len) { throw "[Godhome] size mismatch" }
if ($Sha512) { $h = (Get-FileHash $Out -Algorithm SHA512).Hash; if ($h -ne $Sha512.ToUpper()) { throw "[Godhome] SHA512 mismatch: $h" } ; Write-Output '[Godhome] SHA512 OK' }
if ($Sha256) { $h = (Get-FileHash $Out -Algorithm SHA256).Hash; if ($h -ne $Sha256.ToUpper()) { throw "[Godhome] SHA256 mismatch: $h" } ; Write-Output '[Godhome] SHA256 OK' }
Remove-Item -Recurse -Force $partsDir
Write-Output "[Godhome] done: $Out"
