# [Godhome] 打一个能分享给别人的安装包（手动安装用的 zip）。只在本地生成，不上传到任何地方。
# Release 构建到临时文件夹（不碰游戏里装着的插件），按 BepInEx\plugins\Godhome\ 的结构放好，加上安装说明，压成
# builds\Godhome-LoomOfRecollection-<版本>.zip（builds\ 不进 git）。
# 用法：powershell -File scripts\package.ps1
. "$PSScriptRoot\env.ps1"
$ErrorActionPreference = 'Stop'
Set-Location $Godhome.Root

$stage = Join-Path ([IO.Path]::GetTempPath()) ('godhome_pkg_' + [guid]::NewGuid().ToString('N'))
$buildOut = Join-Path $stage 'build'
New-Item -ItemType Directory -Force $buildOut | Out-Null
try {
    "---- Release 构建 / release build"
    $log = dotnet build src\Godhome\Godhome.csproj -c Release "-p:SilksongPluginsFolder=$buildOut" -v:minimal 2>&1
    $log | Select-String -Pattern 'error|个错误' | ForEach-Object { $_.Line }
    if ($LASTEXITCODE -ne 0) { throw "[Godhome] 构建失败 / build failed" }
    $built = Join-Path $buildOut 'local-Godhome'
    $dll = Join-Path $built 'Godhome.dll'
    if (-not (Test-Path $dll)) { throw "[Godhome] 没有构建出 $dll" }
    $ver = (Get-Item $dll).VersionInfo.FileVersion -replace '^(\d+\.\d+\.\d+).*$', '$1'
    $commit = (git rev-parse --short HEAD).Trim()
    $dirty = [bool](git status --porcelain)

    "---- 组装 / assemble"
    $name = "Godhome-LoomOfRecollection-$ver"
    $root = Join-Path $stage $name
    $plug = Join-Path $root 'BepInEx\plugins\Godhome'
    New-Item -ItemType Directory -Force (Join-Path $plug 'data'), (Join-Path $plug 'languages') | Out-Null
    Copy-Item $dll $plug
    Copy-Item (Join-Path $built 'data\*.json') (Join-Path $plug 'data')
    Copy-Item (Join-Path $built 'languages\*.json') (Join-Path $plug 'languages')
    # 数据表、语言表和源码里的应该一模一样（构建时复制的就是它们）：两边的文件列表一样、每个文件的 hash 一样
    # （Codex 审计 0.10.3：以前只核对三个表，漏了 hk1.json，也不管多出来的、少了的）。
    foreach ($pair in @(@('data', 'data'), @('languages', 'src\Godhome\languages'))) {
        $d, $srcDir = $pair[0], (Join-Path $Godhome.Root $pair[1])
        $src = @(Get-ChildItem $srcDir -Filter *.json -File | ForEach-Object Name | Sort-Object)
        $got = @(Get-ChildItem (Join-Path $plug $d) -Filter *.json -File | ForEach-Object Name | Sort-Object)
        if ($src.Count -eq 0) { throw "[Godhome] $srcDir 里没有 json" }
        if (($src -join '|') -ne ($got -join '|')) { throw "[Godhome] $d\ 的文件和构建出来的不一样：源码 / source [$($src -join ', ')]，包里 / package [$($got -join ', ')]" }
        foreach ($f in $src) {
            if ((Get-FileHash (Join-Path $srcDir $f)).Hash -ne (Get-FileHash (Join-Path $plug "$d\$f")).Hash) { throw "[Godhome] $d\$f 和构建出来的不一样" }
        }
    }
    $readme = [IO.File]::ReadAllText((Join-Path $Godhome.Root 'packaging\安装说明.txt'), [Text.Encoding]::UTF8).Replace('{VERSION}', $ver)
    # 记事本也认 UTF-8 带 BOM；换行用 CRLF。
    [IO.File]::WriteAllText((Join-Path $root 'README.txt'), $readme.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object Text.UTF8Encoding $true))
    # 一键安装脚本（双击 install.bat；它调 install.ps1，从这个 zip 解出来的文件夹里装；uninstall.bat 卸载）。
    # .bat 用英文名：文件名和内容都是纯 ASCII，任何代码页的 cmd 和解压工具都不会弄乱。
    # PowerShell 5.1 读没有 BOM 的脚本会按系统代码页解析中文，这里强制写成 UTF-8 带 BOM、CRLF；.bat 用 CRLF、纯 ASCII。
    $inst = [IO.File]::ReadAllText((Join-Path $Godhome.Root 'packaging\install.ps1'), [Text.Encoding]::UTF8).Replace('{VERSION}', $ver)
    [IO.File]::WriteAllText((Join-Path $root 'install.ps1'), $inst.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object Text.UTF8Encoding $true))
    $bat = [IO.File]::ReadAllText((Join-Path $Godhome.Root 'packaging\install.bat'), [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $root 'install.bat'), $bat.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)
    $unbat = [IO.File]::ReadAllText((Join-Path $Godhome.Root 'packaging\uninstall.bat'), [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $root 'uninstall.bat'), $unbat.Replace("`r`n", "`n").Replace("`n", "`r`n"), [Text.Encoding]::ASCII)

    "---- 压缩 / zip"
    $outDir = Join-Path $Godhome.Root 'builds'
    New-Item -ItemType Directory -Force $outDir | Out-Null
    $zip = Join-Path $outDir "$name.zip"
    if (Test-Path $zip) { Remove-Item $zip }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    # 条目名一律用正斜杠：Windows PowerShell 5.1（.NET Framework）的 ZipFile.CreateFromDirectory 写的是反斜杠，
    # 别的解压工具、模组管理器会把它当成文件名的一部分。
    $fs = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
    $za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
    try {
        Get-ChildItem $root -Recurse -File | Sort-Object FullName | ForEach-Object {
            $rel = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $_.FullName, $rel, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $za.Dispose()
        $fs.Dispose()
    }

    "zip: $zip"
    "version: $ver  commit: $commit$(if ($dirty) { '（工作区有未提交的改动 / dirty）' })"
    "sha256: " + (Get-FileHash $zip -Algorithm SHA256).Hash
    "size: " + (Get-Item $zip).Length
    "---- 内容 / contents"
    $z = [IO.Compression.ZipFile]::OpenRead($zip)
    try { $z.Entries | ForEach-Object { "{0,-60} {1,8}" -f $_.FullName, $_.Length } } finally { $z.Dispose() }
}
finally {
    Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
}
