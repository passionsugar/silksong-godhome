# [Godhome] Thunderstore package, built from the GitHub Release archive (no recompile: the published plugin files are
# byte-for-byte the released ones), packed with tcli build, then checked item by item.
# Default: build and validate only, nothing is uploaded. -Publish uploads, and only inside GitHub Actions:
# the token comes from the TCLI_AUTH_TOKEN environment variable (set from secrets.TCLI_AUTH_TOKEN by the workflow);
# it is never passed on the command line and never printed.
# Works in Windows PowerShell 5.1 and PowerShell 7 (GitHub's Linux runners).
# Usage: pwsh scripts/thunderstore-package.ps1 -ReleaseZip <Godhome-LoomOfRecollection-x.y.z.zip> -Version x.y.z [-OutDir dist] [-Publish]
param(
    [Parameter(Mandatory = $true)][string]$ReleaseZip,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$OutDir = 'thunderstore-out',
    [switch]$Publish
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$repo = Split-Path -Parent $PSScriptRoot
$tsDir = Join-Path (Join-Path (Join-Path $repo 'src') 'Godhome') 'thunderstore'
$toolDir = Join-Path (Join-Path $repo 'src') 'Godhome'
$pluginPrefix = 'BepInEx/plugins/Godhome/'
$fail = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) {
    if ($ok) { "  PASS $what" } else { "  FAIL $what"; $fail.Add($what) }
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "version must be Major.Minor.Patch, got '$Version'" }
$ReleaseZip = (Resolve-Path $ReleaseZip).Path
if ((Split-Path -Leaf $ReleaseZip) -ne "Godhome-LoomOfRecollection-$Version.zip") { throw "release archive name does not match version $Version : $ReleaseZip" }

# ---- settings from thunderstore.toml (single source; the checks below compare tcli's manifest against them)
$toml = [IO.File]::ReadAllText((Join-Path $tsDir 'thunderstore.toml'), [Text.Encoding]::UTF8)
function TomlValue([string]$key) {
    $m = [regex]::Match($toml, '(?m)^\s*' + [regex]::Escape($key) + '\s*=\s*"([^"]*)"')
    if (-not $m.Success) { throw "thunderstore.toml: $key missing" }
    $m.Groups[1].Value
}
$Namespace = TomlValue 'namespace'
$Name = TomlValue 'name'
$Website = TomlValue 'websiteUrl'
$depBlock = [regex]::Match($toml, '(?ms)^\[package\.dependencies\]\s*$(.*?)^\[')
if (-not $depBlock.Success) { throw 'thunderstore.toml: [package.dependencies] missing' }
$wantDeps = @([regex]::Matches($depBlock.Groups[1].Value, '(?m)^\s*([A-Za-z0-9_]+-[A-Za-z0-9_]+)\s*=\s*"(\d+\.\d+\.\d+)"') | ForEach-Object { $_.Groups[1].Value + '-' + $_.Groups[2].Value } | Sort-Object)
"package: $Namespace-$Name $Version"
"dependencies: $($wantDeps -join ', ')"

$stage = Join-Path ([IO.Path]::GetTempPath()) ('godhome_ts_' + [guid]::NewGuid().ToString('N'))
try {
    # ---- 1. release archive -> staging (plugins/ = the archive's BepInEx/plugins/Godhome)
    "---- release archive: $ReleaseZip"
    "sha256: " + (Get-FileHash $ReleaseZip -Algorithm SHA256).Hash
    $ts = Join-Path $stage 'thunderstore'
    $plugins = Join-Path (Join-Path $ts 'temp') 'plugins'
    New-Item -ItemType Directory -Force $plugins | Out-Null
    $released = @{}
    $z = [IO.Compression.ZipFile]::OpenRead($ReleaseZip)
    try {
        foreach ($e in $z.Entries) {
            if ($e.FullName.Contains('\')) { throw "release archive entry with a backslash: $($e.FullName)" }
            if (-not $e.FullName.StartsWith($pluginPrefix, [StringComparison]::Ordinal) -or $e.FullName.EndsWith('/')) { continue }
            $rel = $e.FullName.Substring($pluginPrefix.Length)
            if ($rel -match '(^|/)\.\.(/|$)') { throw "bad path in the release archive: $($e.FullName)" }
            $dst = Join-Path $plugins ($rel.Replace('/', [IO.Path]::DirectorySeparatorChar))
            New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dst, $true)
            $released['plugins/' + $rel] = (Get-FileHash $dst -Algorithm SHA256).Hash
        }
    } finally { $z.Dispose() }
    Check ($released.ContainsKey('plugins/Godhome.dll')) 'release archive has BepInEx/plugins/Godhome/Godhome.dll'
    foreach ($j in 'bosses', 'kit', 'pantheons', 'hk1') { Check ($released.ContainsKey("plugins/data/$j.json")) "release archive has data/$j.json" }
    Check (@($released.Keys | Where-Object { $_ -like 'plugins/languages/*.json' }).Count -gt 0) 'release archive has languages/*.json'
    if ($fail.Count -gt 0) { throw "release archive is incomplete: $($fail -join '; ')" }
    $dll = Join-Path $plugins 'Godhome.dll'
    $pv = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll).ProductVersion
    Check ($pv -eq $Version -or $pv.StartsWith("$Version+", [StringComparison]::Ordinal)) "Godhome.dll version $pv matches $Version"

    $changelog = Join-Path $repo 'CHANGELOG.md'
    Check (Test-Path $changelog) 'CHANGELOG.md exists'
    if (Test-Path $changelog) {
        Copy-Item $changelog (Join-Path (Join-Path $ts 'temp') 'CHANGELOG.md')
        $cl = [IO.File]::ReadAllText($changelog, [Text.Encoding]::UTF8)
        Check ($cl -match ('(?m)^##\s+\[?v?' + [regex]::Escape($Version) + '\b')) "CHANGELOG.md has a section for $Version"
    }
    foreach ($f in 'thunderstore.toml', 'icon.png', 'README.md') { Copy-Item (Join-Path $tsDir $f) (Join-Path $ts $f) }

    # ---- 2. tcli build (pinned in src/Godhome/.config/dotnet-tools.json)
    "---- tcli build"
    $runtimes = (& dotnet --list-runtimes) -join "`n"
    if ($runtimes -notmatch 'Microsoft\.NETCore\.App 6\.') {
        # tcli 0.2.4 targets net6.0; let it run on the newer runtime that is installed.
        $env:DOTNET_ROLL_FORWARD = 'Major'
        'no .NET 6 runtime: DOTNET_ROLL_FORWARD=Major'
    }
    Push-Location $toolDir
    try {
        & dotnet tool restore | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed' }
        & dotnet tcli build --config-path (Join-Path $ts 'thunderstore.toml') --package-namespace $Namespace --package-name $Name --package-version $Version | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'tcli build failed' }
    } finally { Pop-Location }
    $built = @(Get-ChildItem (Join-Path $ts 'dist') -Filter '*.zip')
    if ($built.Count -ne 1) { throw "expected one package in dist, found $($built.Count)" }
    $pkg = $built[0].FullName

    # ---- 3. checks (https://wiki.thunderstore.io/mods/creating-a-package)
    "---- checks: $($built[0].Name)"
    Check ($built[0].Name -eq "$Namespace-$Name-$Version.zip") "package file name $Namespace-$Name-$Version.zip"
    Check ($Namespace -match '^[A-Za-z0-9_]{1,128}$' -and $Name -match '^[A-Za-z0-9_]{1,128}$') 'namespace and name use A-Z a-z 0-9 _ only'
    $entries = @{}
    $z = [IO.Compression.ZipFile]::OpenRead($pkg)
    try {
        foreach ($e in $z.Entries) {
            if ($e.FullName.EndsWith('/')) { continue }
            Check (-not $e.FullName.Contains('\')) "entry uses forward slashes: $($e.FullName)"
            $ms = New-Object IO.MemoryStream
            $s = $e.Open(); try { $s.CopyTo($ms) } finally { $s.Dispose() }
            $entries[$e.FullName.Replace('\', '/')] = $ms.ToArray()
        }
    } finally { $z.Dispose() }
    foreach ($f in 'manifest.json', 'icon.png', 'README.md', 'CHANGELOG.md') { Check ($entries.ContainsKey($f)) "$f at the package root" }
    $extra = @($entries.Keys | Where-Object { $_ -notin 'manifest.json', 'icon.png', 'README.md', 'CHANGELOG.md' -and -not $released.ContainsKey($_) })
    Check ($extra.Count -eq 0) "no unexpected files$(if ($extra.Count) { ': ' + ($extra -join ', ') })"
    $sha = [Security.Cryptography.SHA256]::Create()
    foreach ($k in $released.Keys) {
        $same = $entries.ContainsKey($k) -and (([BitConverter]::ToString($sha.ComputeHash($entries[$k])) -replace '-', '') -eq $released[$k])
        Check $same "$k identical to the release archive"
    }
    $strict = New-Object Text.UTF8Encoding($false, $true)
    if ($entries.ContainsKey('manifest.json')) {
        $m = $strict.GetString($entries['manifest.json']).TrimStart([char]0xFEFF) | ConvertFrom-Json
        Check ($m.name -eq $Name) "manifest name '$($m.name)'"
        Check ($m.version_number -eq $Version) "manifest version_number '$($m.version_number)'"
        Check ($m.website_url -eq $Website) "manifest website_url '$($m.website_url)'"
        Check ($m.description.Length -ge 1 -and $m.description.Length -le 250) "manifest description length $($m.description.Length) (1-250)"
        $gotDeps = @($m.dependencies | Sort-Object)
        Check (($gotDeps -join '|') -eq ($wantDeps -join '|')) "manifest dependencies [$($gotDeps -join ', ')]"
    }
    if ($entries.ContainsKey('icon.png')) {
        $b = $entries['icon.png']
        $png = $b.Length -gt 24 -and (([BitConverter]::ToString($b, 0, 8)) -eq '89-50-4E-47-0D-0A-1A-0A')
        $w = if ($png) { ([int]$b[16] -shl 24) -bor ([int]$b[17] -shl 16) -bor ([int]$b[18] -shl 8) -bor [int]$b[19] } else { 0 }
        $h = if ($png) { ([int]$b[20] -shl 24) -bor ([int]$b[21] -shl 16) -bor ([int]$b[22] -shl 8) -bor [int]$b[23] } else { 0 }
        Check ($png -and $w -eq 256 -and $h -eq 256) "icon.png is a 256x256 PNG (${w}x${h})"
    }
    foreach ($f in 'README.md', 'CHANGELOG.md') {
        if ($entries.ContainsKey($f)) {
            $ok = $true; try { [void]$strict.GetString($entries[$f]) } catch { $ok = $false }
            Check $ok "$f is valid UTF-8"
        }
    }
    $size = (Get-Item $pkg).Length
    Check ($size -lt 500MB) "package size $size bytes"

    New-Item -ItemType Directory -Force $OutDir | Out-Null
    $out = Join-Path (Resolve-Path $OutDir).Path $built[0].Name
    Copy-Item $pkg $out -Force
    "package: $out"
    "sha256: " + (Get-FileHash $out -Algorithm SHA256).Hash
    if ($fail.Count -gt 0) { throw "$($fail.Count) check(s) failed: $($fail -join '; ')" }
    'all checks passed'

    # ---- 4. upload (GitHub Actions only, explicit -Publish only)
    if (-not $Publish) { 'not published (validation only)'; return }
    if ($env:GITHUB_ACTIONS -ne 'true') { throw '-Publish only runs inside GitHub Actions' }
    if ([string]::IsNullOrEmpty($env:TCLI_AUTH_TOKEN)) { throw 'TCLI_AUTH_TOKEN is not set (repository secret missing?)' }
    $api = "https://thunderstore.io/api/experimental/package/$Namespace/$Name/$Version/"
    $exists = $false
    try { Invoke-WebRequest -Uri $api -UseBasicParsing -Method Get | Out-Null; $exists = $true }
    catch {
        $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
        if ($code -ne 404) { throw "could not check whether $Version is already on Thunderstore (HTTP $code)" }
    }
    if ($exists) { throw "$Namespace-$Name $Version is already on Thunderstore; versions cannot be replaced" }
    "---- tcli publish $Namespace-$Name $Version"
    Push-Location $toolDir
    try {
        & dotnet tcli publish --config-path (Join-Path $ts 'thunderstore.toml') --file $out --package-namespace $Namespace --package-name $Name --package-version $Version | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'tcli publish failed' }
    } finally { Pop-Location }
    "published: https://thunderstore.io/c/hollow-knight-silksong/p/$Namespace/$Name/"
}
finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}
