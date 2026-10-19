#Requires -Version 7.0
<#
.SYNOPSIS
  构建 实践小梅IshWY（IshWY）的 Windows x64 便携版（unpackaged、自包含运行时）并打包 zip。

.DESCRIPTION
  发布目录结构：直接解压即可运行，配置与日志就近放在 data\ 下。
  发布后校验 IshWY.pri / *.xbf 是否存在 —— 未打包发布时 Windows App SDK
  不会自动把它们放进发布目录，缺失会导致启动即 XamlParseException。
  默认执行 --smoke（启动 3 秒后退出）验证发布产物可启动；CI 无交互桌面时可 -SkipSmoke。

.EXAMPLE
  pwsh scripts/publish.ps1 -Version 0.1.0
.EXAMPLE
  pwsh scripts/publish.ps1 -Version 0.1.0 -SkipSmoke
#>
param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SkipSmoke,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/PrtScr.App/PrtScr.App.csproj'

if (-not $Version) {
    $text = Get-Content $project -Raw
    if ($text -match '<Version>([^<]+)</Version>') {
        $Version = $Matches[1].Trim()
    } else {
        throw '未指定 -Version，且 csproj 中没有 <Version> 节点'
    }
}

$name = "IshWY-$Version-$Runtime"
$outDir = Join-Path $root "artifacts/$name"
$zipPath = Join-Path $root "artifacts/$name.zip"

Write-Host "==> 发布 $name ($Configuration)" -ForegroundColor Cyan
if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

& dotnet publish $project -c $Configuration -r $Runtime --self-contained true -o $outDir `
    -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（exit $LASTEXITCODE）"
}

# 未打包发布不会自动带上 WinUI 资源：PRI 与 XBF 缺失会导致启动崩溃。
$pri = Join-Path $outDir 'IshWY.pri'
if (-not (Test-Path $pri)) {
    throw "发布输出缺少 IshWY.pri，请检查 PrtScr.App.csproj 的 CopyPriAndXbfToPublish 目标"
}
$xbfCount = (Get-ChildItem $outDir -Recurse -Filter *.xbf | Measure-Object).Count
if ($xbfCount -eq 0) {
    throw '发布输出缺少 .xbf 文件，请检查 PrtScr.App.csproj 的 CopyPriAndXbfToPublish 目标'
}
Write-Host "    资源检查通过：IshWY.pri + $xbfCount 个 .xbf" -ForegroundColor Green

# 发布包不携带 PDB：PDB 会暴露构建机的绝对路径（含用户名/.nuget 缓存路径）。
$pdbFiles = @(Get-ChildItem $outDir -Filter *.pdb -File -Recurse)
if ($pdbFiles.Count -gt 0) {
    $pdbFiles | Remove-Item -Force
    Write-Host "    已移除 $($pdbFiles.Count) 个 PDB（避免泄露构建路径）" -ForegroundColor Yellow
}

$exe = Join-Path $outDir 'IshWY.exe'
if (-not (Test-Path $exe)) {
    throw "发布输出缺少 IshWY.exe"
}

if (-not $SkipSmoke) {
    Write-Host '==> 冒烟测试（--smoke，约 3 秒）' -ForegroundColor Cyan
    $proc = Start-Process -FilePath $exe -ArgumentList '--smoke' -PassThru -Wait
    if ($proc.ExitCode -ne 0) {
        throw "冒烟测试失败，exit code = $($proc.ExitCode)，日志见 $outDir/data/logs/app.log"
    }
    Write-Host '    冒烟测试通过' -ForegroundColor Green
    # 冒烟运行会生成默认配置与日志，打包前清掉，保持便携包干净。
    $dataDir = Join-Path $outDir 'data'
    if (Test-Path $dataDir) {
        Remove-Item $dataDir -Recurse -Force
    }
}

$sizeMb = [math]::Round(((Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "    发布目录：$outDir ($sizeMb MB)" -ForegroundColor Green

if (-not $NoZip) {
    Write-Host '==> 打包 zip' -ForegroundColor Cyan
    if (Test-Path $zipPath) {
        Remove-Item $zipPath -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $outDir, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
    $zipMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "    压缩包：$zipPath ($zipMb MB)" -ForegroundColor Green
}

Write-Host '==> 完成' -ForegroundColor Cyan
