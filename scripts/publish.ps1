#Requires -Version 7.0
<#
.SYNOPSIS
  构建 实践小梅IshWY（IshWY）的 Windows x64 便携版（unpackaged、自包含运行时）并打包 zip。

.DESCRIPTION
  发布目录结构：直接解压即可运行，配置与日志就近放在 data\ 下。
  发布后校验 IshWY.pri / *.xbf 是否存在 —— 未打包发布时 Windows App SDK
  不会自动把它们放进发布目录，缺失会导致启动即 XamlParseException。

  版本透传（B2）：-Version 同时传给 dotnet publish 的 -p:Version 与 -p:FileVersion
  （FileVersion 取 X.Y.Z.0，Win32 四段数值不含预发布后缀），使产物版本与 manifest 一致。
  -ZipOnly 只对已 stage 的目录打包，不再构建/裁剪；gate.ps1 用它实现「smoke 通过并核验
  源码稳定之后才生成 ZIP」（B6）。

  默认执行 --smoke v1（临时隔离根 + 结果文件校验，30 秒上限）验证发布产物可启动；
  CI/无交互桌面必须显式 -SkipSmoke；此时 manifest/报告应记为 smoke 未验证，不得冒充验收通过。

.EXAMPLE
  pwsh scripts/publish.ps1 -Version 1.3.1
.EXAMPLE
  pwsh scripts/publish.ps1 -Version 1.3.1 -SkipSmoke -NoZip
.EXAMPLE
  pwsh scripts/publish.ps1 -Version 1.3.1 -ZipOnly
#>
param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SkipSmoke,
    [switch]$NoZip,
    # 只把已 stage 的发布目录打成 zip；不删除、不重建、不裁剪该目录。
    [switch]$ZipOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/SourceIdentity.ps1')

function New-PortableZip {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDirectory,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDirectory, $DestinationPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)

    $zip = Get-Item -LiteralPath $DestinationPath
    $hash = (Get-FileHash -LiteralPath $DestinationPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("    压缩包：{0} ({1:N1} MB)" -f $DestinationPath, ($zip.Length / 1MB)) -ForegroundColor Green
    # 稳定机器可读标记：gate.ps1 据此确认 zip 归属本次运行并复核哈希。
    Write-Host ("ZIP_PATH=" + $DestinationPath)
    Write-Host ("ZIP_BYTES=" + $zip.Length)
    Write-Host ("ZIP_SHA256=" + $hash)
    return $zip
}

if ($Runtime -notmatch '^[a-z0-9]+-[a-z0-9]+$') {
    throw "Runtime 非法：'$Runtime'；只接受形如 win-x64 / win-arm64 的标识"
}

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
Assert-VersionString $Version
$shape = Get-VersionShape $Version

$name = "IshWY-$Version-$Runtime"
$outDir = Join-Path $root "artifacts/$name"
$zipPath = Join-Path $root "artifacts/$name.zip"

if ($ZipOnly) {
    # B6：只打包本次已 stage 并已通过 smoke 的目录，不执行任何构建或裁剪。
    if (-not (Test-Path -LiteralPath $outDir -PathType Container)) {
        throw "ZipOnly 需要已存在的发布目录：$outDir"
    }
    $required = @((Join-Path $outDir 'IshWY.exe'), (Join-Path $outDir 'IshWY.pri'))
    foreach ($requiredPath in $required) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "ZipOnly 拒绝打包不完整目录，缺少：$requiredPath"
        }
    }
    Write-Host "==> 打包 $name.zip（仅打包已 stage 目录）" -ForegroundColor Cyan
    [void](New-PortableZip -SourceDirectory $outDir -DestinationPath $zipPath)
    Write-Host '==> 完成' -ForegroundColor Cyan
    exit 0
}

Write-Host "==> 发布 $name ($Configuration)" -ForegroundColor Cyan
if (Test-Path $outDir) {
    Remove-Item $outDir -Recurse -Force
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$publishArguments = @(
    $project,
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-o', $outDir,
    "-p:Version=$Version",
    "-p:FileVersion=$($shape.FileVersion)",
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:PublishTrimmed=true',
    '-p:TrimMode=partial'
)
& dotnet publish @publishArguments
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

# Windows App SDK 元包会把 AI/ML/Search/Widgets 一并带进发布目录，其中
# onnxruntime.dll / DirectML.dll 约 38 MB，本应用从未加载它们。
# 直接删除这些未使用的可选组件，缩小便携包（启动冒烟会验证依赖完整）。
$trimPatterns = @(
    'DirectML.dll',
    'onnxruntime.dll',
    'Microsoft.ML.OnnxRuntime.dll',
    'Microsoft.Asg.*.dll',
    'Microsoft.Windows.AI*.dll',
    'Microsoft.Windows.AI*.winmd',
    'Microsoft.Windows.Internal.AI*.winmd',
    'Microsoft.Windows.Search*.dll',
    'Microsoft.Windows.Search*.winmd',
    'Microsoft.Windows.Internal.SemanticSearch*.winmd',
    'Microsoft.Windows.SemanticSearch*.winmd',
    'Microsoft.Windows.Widgets*.dll',
    'Microsoft.Windows.Widgets*.winmd',
    'Microsoft.DiaSymReader.Native.amd64.dll',
    # 以下组件本应用从未加载：调试支持、WebView2、AI 推理周边、通知代理、工作负载管理。
    # 注意：Microsoft.UI.Xaml.Phone.dll 不能删。NavigationView 在设置页初始化选中项时会
    # 动态加载它，删掉后正常启动会抛 COMException 0x8007007E（模块找不到），而 --smoke
    # 因定时强退曾掩盖该错误。
    'mscordaccore*.dll',
    'mscordbi.dll',
    'createdump.exe',
    'Microsoft.UI.Designer.dll',
    'Microsoft.Web.WebView2.Core.dll',
    'WebView2Loader.dll',
    'msquic.dll',
    'PerceptiveStreaming.dll',
    'NPUDetect.dll',
    'Microsoft.Windows.Workloads*.dll',
    'Microsoft.Windows.Workloads.pri',
    'workloads.*.json',
    'Microsoft.Windows.ApplicationModel.Background.UniversalBGTask.dll',
    'SessionHandleIPCProxyStub.dll',
    'PushNotificationsLongRunningTask.ProxyStub.dll',
    'RestartAgent.exe',
    'Microsoft.Graphics.Imaging.dll',
    'System.Numerics.Tensors.dll',
    'System.IO.Compression.Native.dll'
)
$trimmedFiles = @()
foreach ($pattern in $trimPatterns) {
    $trimmedFiles += @(Get-ChildItem $outDir -File -Filter $pattern -ErrorAction SilentlyContinue)
}
if ($trimmedFiles.Count -gt 0) {
    $trimmedMb = [math]::Round((($trimmedFiles | Measure-Object Length -Sum).Sum / 1MB), 1)
    $trimmedFiles | Remove-Item -Force
    Write-Host "    已移除 $($trimmedFiles.Count) 个未使用的 App SDK 可选组件（约 $trimmedMb MB）" -ForegroundColor Yellow
}

# .winmd 是编译期 WinRT 元数据，发布运行时不需要；.NET 投影已编进 Microsoft.Windows.SDK.NET.dll。
$winmdFiles = @(Get-ChildItem $outDir -File -Filter *.winmd -ErrorAction SilentlyContinue)
if ($winmdFiles.Count -gt 0) {
    $winmdMb = [math]::Round((($winmdFiles | Measure-Object Length -Sum).Sum / 1MB), 1)
    $winmdFiles | Remove-Item -Force
    Write-Host "    已移除 $($winmdFiles.Count) 个 WinRT 元数据文件（约 $winmdMb MB）" -ForegroundColor Yellow
}

# WinUI/App SDK 自带数十种语言的 .mui；产品只使用中英文，保留这四种即可。
# 其它语言缺失时会回退到 en-us，不会影响启动。
$keepLocales = @('en-us', 'en-GB', 'zh-CN', 'zh-TW')
$localeDirs = @(Get-ChildItem $outDir -Directory | Where-Object {
    $_.Name -match '^[a-z]{2,3}(-[A-Za-z0-9]+)*$' -and $_.Name -notin $keepLocales
})
if ($localeDirs.Count -gt 0) {
    $localeMb = [math]::Round((($localeDirs | ForEach-Object {
        Get-ChildItem $_.FullName -Recurse -File | Measure-Object Length -Sum
    } | Measure-Object Sum -Sum).Sum / 1MB), 1)
    $localeDirs | Remove-Item -Recurse -Force
    Write-Host "    已移除 $($localeDirs.Count) 个未使用语言目录（约 $localeMb MB）" -ForegroundColor Yellow
}

$exe = Join-Path $outDir 'IshWY.exe'
if (-not (Test-Path $exe)) {
    throw "发布输出缺少 IshWY.exe"
}

# 产物版本自检：与将要写入 manifest 的版本不一致时立即失败，避免「目录名一个版本、
# 二进制另一个版本」。gate 会对同一断言再做一次独立核验。
$dll = Join-Path $outDir 'IshWY.dll'
if (-not (Test-Path $dll)) {
    throw "发布输出缺少 IshWY.dll，无法核对版本身份"
}
$dllInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
$versionMatch = Test-ArtifactVersionMatch -Expected $Version -ProductVersion $dllInfo.ProductVersion -FileVersion $dllInfo.FileVersion
if (-not $versionMatch.Ok) {
    throw ("发布产物版本与 -Version {0} 不一致：{1}" -f $Version, ($versionMatch.Reasons -join '; '))
}
Write-Host ("    版本身份：IshWY.dll ProductVersion={0} FileVersion={1}" -f $dllInfo.ProductVersion, $dllInfo.FileVersion) -ForegroundColor Green

if (-not $SkipSmoke) {
    Write-Host '==> 冒烟测试（--smoke v1，30 秒上限）' -ForegroundColor Cyan
    $smokeScript = Join-Path $PSScriptRoot 'smoke.ps1'
    if (-not (Test-Path $smokeScript)) {
        throw "缺少冒烟脚本 $smokeScript"
    }

    # 冒烟写自己的临时根，不再在发布目录生成 data；用子 pwsh 调脚本以便读取退出码。
    $pwshExe = (Get-Process -Id $PID).Path
    & $pwshExe -NoProfile -File $smokeScript -ExePath $exe
    if ($LASTEXITCODE -ne 0) {
        throw "冒烟测试失败，exit code = $LASTEXITCODE"
    }
}

$sizeMb = [math]::Round(((Get-ChildItem $outDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "    发布目录：$outDir ($sizeMb MB)" -ForegroundColor Green

if (-not $NoZip) {
    Write-Host '==> 打包 zip' -ForegroundColor Cyan
    [void](New-PortableZip -SourceDirectory $outDir -DestinationPath $zipPath)
}

Write-Host '==> 完成' -ForegroundColor Cyan
