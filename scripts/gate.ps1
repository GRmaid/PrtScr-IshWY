#Requires -Version 7.0
<#
P-11 发布门禁（严格版）。

顺序（B6）：core gates → publish stage（-SkipSmoke -NoZip）→ 产物/版本断言（B2/B5）
→ 受控外层 smoke（B5）→ 源码稳定复检（HEAD+fingerprint+Dirty）→ 本次 ZIP 生成与 SHA256 核验
→ manifest（含清单自身 SHA256）。

严格语义（B4）：
- 只有全部通过且本次 ZIP 已生成才输出 GATE PASS 并 exit 0；
- -SkipSmoke / -NoZip 只能输出 GATE LIMITED 并 exit 2，绝不能 GATE PASS；
- 任何断言失败输出 GATE FAIL 并 exit 1。

本脚本不含 src 读写；不在 CI 无交互桌面上自动启动 App，smoke 需要能创建窗口的桌面会话。
#>

param(
    [string]$Mode = 'ci',
    [string]$Version = '',
    [string]$Runtime = 'win-x64',
    [switch]$SkipSmoke,
    [switch]$NoZip
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'lib/SmokeResult.ps1')
. (Join-Path $PSScriptRoot 'lib/SourceIdentity.ps1')
. (Join-Path $PSScriptRoot 'lib/GateSupport.ps1')

if ($Mode -notin @('ci', 'release')) {
    throw "未知 Mode：'$Mode'（只接受 ci / release）"
}
if ($Runtime -notmatch '^[A-Za-z0-9.-]+$') {
    throw "Runtime 形状非法：'$Runtime'"
}

$project = Join-Path $root 'src/PrtScr.App/PrtScr.App.csproj'
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $Version = "$Version".Trim()
}
Assert-VersionString $Version
$shape = Get-VersionShape $Version

$startedAtUtc = (Get-Date).ToUniversalTime()
$identityBefore = Get-SourceIdentity -Root $root
$releaseBlocker = Test-ReleaseSourceAllowed -Mode $Mode -Dirty $identityBefore.Dirty
if ($null -ne $releaseBlocker) {
    throw "release 模式要求干净工作树：$releaseBlocker"
}

# B3：被忽略但形似构建输入的文件、以及读不到的构建输入，都是覆盖缺口，完整门禁必须失败。
$ignoredBuildInputs = @($identityBefore.IgnoredInputCandidates)
$uncoveredInputs = @($identityBefore.UncoveredInputs)
$excludedInputs = @($identityBefore.ExcludedInputs)
$sourceCovered = ($ignoredBuildInputs.Count -eq 0) -and ($uncoveredInputs.Count -eq 0)

$evidenceRoot = Join-Path $root 'artifacts/evidence'
$evidenceDir = New-EvidenceDirectory -EvidenceRoot $evidenceRoot -Mode $Mode
$logsDir = Join-Path $evidenceDir 'logs'
$hashesDir = Join-Path $evidenceDir 'hashes'
$smokeEvidenceDir = Join-Path $evidenceDir 'smoke'
New-Item -ItemType Directory -Path $logsDir, $hashesDir, $smokeEvidenceDir -Force | Out-Null

$script:failed = $false
$gates = [System.Collections.Generic.List[object]]::new()

function Add-GateSkipped {
    param([string]$Name, [string]$Command, [string]$Reason)
    $gates.Add([ordered]@{
            name = $Name; command = $Command; exitCode = $null
            status = 'skipped'; seconds = 0; log = $null; reason = $Reason
        })
}

function Add-GateResult {
    param([string]$Name, [string]$Command, $ExitCode, [string]$Status, [string]$Reason, [double]$Seconds = 0)
    $gates.Add([ordered]@{
            name = $Name; command = $Command; exitCode = $ExitCode
            status = $Status; seconds = $Seconds; log = ('logs/' + $Name + '.log'); reason = $Reason
        })
}

function Invoke-GateStep {
    # 不用管道回传：日志直接重定向到文件，退出码单独读取，避免污染函数返回值（M7）。
    param([string]$Name, [string]$FilePath, [string[]]$Arguments, [string]$LogName, [string]$Reason = $null)
    $logPath = Join-Path $logsDir $LogName
    $display = Format-CommandLine -FilePath $FilePath -Arguments $Arguments
    Write-Host ("==> " + $display) -ForegroundColor Cyan
    $started = Get-Date
    & $FilePath @Arguments *> $logPath
    $code = $LASTEXITCODE
    if (Test-Path -LiteralPath $logPath -PathType Leaf) {
        Get-Content -LiteralPath $logPath -Tail 40 | ForEach-Object { Write-Host ('    ' + $_) }
    }
    $seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    $status = if ($code -eq 0) { 'passed' } else { 'failed' }
    Add-GateResult -Name $Name -Command $display -ExitCode $code -Status $status -Reason $Reason -Seconds $seconds
    if ($code -ne 0) { $script:failed = $true }
    return $status
}

function Get-LogMarker {
    param([string]$LogPath, [string]$Name)
    if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) { return $null }
    $prefix = $Name + '='
    $line = Get-Content -LiteralPath $LogPath |
        Where-Object { $_.StartsWith($prefix, [StringComparison]::Ordinal) } |
        Select-Object -Last 1
    if ($null -eq $line) { return $null }
    return $line.Substring($prefix.Length).Trim()
}

function New-EvidenceFileRecord {
    param([string]$EvidenceDir, [string]$RelativePath)
    $full = Join-Path $EvidenceDir $RelativePath
    if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { return $null }
    return [ordered]@{
        path = $RelativePath
        sha256 = (Get-FileSha256 $full)
        bytes = (Get-Item -LiteralPath $full).Length
    }
}

Write-Host ("证据目录：" + $evidenceDir)
Write-Host ("源码：" + $identityBefore.Head + ($(if ($identityBefore.Dirty) { '（dirty，未提交改动计入指纹）' } else { '（clean）' })))
if (-not $sourceCovered) {
    Write-Host ('源码输入覆盖缺口：ignored=' + $ignoredBuildInputs.Count + ' uncovered=' + $uncoveredInputs.Count) -ForegroundColor Red
}

$buildStatus = Invoke-GateStep -Name 'build' -FilePath 'dotnet' -Arguments @('build', 'PrtScr.sln', '-c', 'Debug', '--nologo') -LogName 'build.log'

$testStatus = 'skipped'
if ($buildStatus -eq 'passed') {
    $testStatus = Invoke-GateStep -Name 'test' -FilePath 'dotnet' -Arguments @('test', 'PrtScr.sln', '-c', 'Debug', '--no-build', '--nologo') -LogName 'test.log'
}
else {
    Add-GateSkipped -Name 'test' -Command 'dotnet test PrtScr.sln -c Debug --no-build --nologo' -Reason 'build-failed'
}

$formatStatus = 'skipped'
if ($buildStatus -eq 'passed') {
    $formatStatus = Invoke-GateStep -Name 'format' -FilePath 'dotnet' -Arguments @('format', 'whitespace', 'PrtScr.sln', '--verify-no-changes', '--no-restore') -LogName 'format.log'
}
else {
    Add-GateSkipped -Name 'format' -Command 'dotnet format whitespace PrtScr.sln --verify-no-changes --no-restore' -Reason 'build-failed'
}

# B6 stage：始终 -SkipSmoke -NoZip，先只发布到目录，不产生任何 ZIP 证据。
$publishStatus = 'skipped'
$outDir = Join-Path $root ("artifacts/IshWY-{0}-{1}" -f $Version, $Runtime)
$zipPath = "$outDir.zip"
$publishScript = Join-Path $PSScriptRoot 'publish.ps1'
$stageArgs = @('-NoProfile', '-File', $publishScript, '-Version', $Version, '-Runtime', $Runtime, '-Configuration', 'Release', '-SkipSmoke', '-NoZip')
if ($buildStatus -eq 'passed') {
    $publishStatus = Invoke-GateStep -Name 'publish' -FilePath 'pwsh' -Arguments $stageArgs -LogName 'publish.log' -Reason 'stage-only'
}
else {
    Add-GateSkipped -Name 'publish' -Command (Format-CommandLine -FilePath 'pwsh' -Arguments $stageArgs) -Reason 'build-failed'
}

# B2/B5：产物断言，必须在 smoke 之前。
$artifactCheck = $null
$artifactVerified = $false
if ($publishStatus -eq 'passed') {
    $artifactCheck = Test-PublishArtifact -OutDir $outDir -Version $Version
    $artifactVerified = $artifactCheck.Ok
    $artifactCommand = 'Test-PublishArtifact -OutDir ' + $outDir + ' -Version ' + $Version
    if ($artifactVerified) {
        Add-GateResult -Name 'artifact-identity' -Command $artifactCommand -ExitCode 0 -Status 'passed' -Reason $null
    }
    else {
        Add-GateResult -Name 'artifact-identity' -Command $artifactCommand -ExitCode 1 -Status 'failed' -Reason ($artifactCheck.Reasons -join '; ')
        $script:failed = $true
    }
}
else {
    Add-GateSkipped -Name 'artifact-identity' -Command 'Test-PublishArtifact' -Reason 'publish-not-passed'
}

# B8：smoke 前保存确定性输出清单（相对路径/长度/SHA256），smoke 后重新采样比对。
$artifactHashPath = Join-Path $hashesDir 'artifact-files.sha256'
$artifactManifestBeforePath = Join-Path $hashesDir 'artifact-manifest-before.json'
$artifactManifestAfterPath = Join-Path $hashesDir 'artifact-manifest-after.json'
$artifactManifestComparePath = Join-Path $hashesDir 'artifact-manifest-compare.json'
$artifactManifestBefore = $null
$artifactManifestAfter = $null
$outputComparison = $null
$outputStable = $true
if ($publishStatus -eq 'passed' -and (Test-Path -LiteralPath $outDir -PathType Container)) {
    $artifactManifestBefore = Get-OutputManifest -Root $outDir
    $artifactManifestBefore | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $artifactManifestBeforePath -Encoding utf8
    if ($artifactManifestBefore.FileCount -gt 0) {
        $artifactHashLines = foreach ($entry in @($artifactManifestBefore.Entries)) {
            '{0}  {1}' -f $entry.sha256, $entry.path
        }
        Set-Content -LiteralPath $artifactHashPath -Value $artifactHashLines -Encoding utf8
    }
    if (-not $artifactManifestBefore.Ok) {
        $outputStable = $false
        $script:failed = $true
        Add-GateResult -Name 'artifact-output-manifest' -Command ('Get-OutputManifest -Root ' + $outDir) -ExitCode 1 -Status 'failed' -Reason ('输出清单读取失败：' + (@($artifactManifestBefore.Reasons) -join '; '))
    }
}

# B5 smoke：受控外层进程，先异步读取再等待退出（M2）；成功后才清理临时根（M3）。
$smokeStatus = 'not-run'
$smokeSkipReason = $null
$smokeRoot = $null
$smokeResultPath = $null
$smokeArgv = @()
$smokeArgvVerified = $null
$smokeResultEvidence = Join-Path $smokeEvidenceDir 'smoke-result.json'
$smokeLogPath = Join-Path $logsDir 'smoke.log'
if ($SkipSmoke) {
    $smokeSkipReason = 'skipped-by-request'
    Add-GateSkipped -Name 'smoke' -Command 'pwsh scripts/smoke.ps1 -ExePath <out>/IshWY.exe -KeepRoot' -Reason $smokeSkipReason
    Write-Host '==> smoke 未运行（-SkipSmoke）：这是诊断/构建证据，不是发布验收证据，门禁只能 LIMITED。' -ForegroundColor Yellow
}
elseif (-not $artifactVerified) {
    $smokeSkipReason = 'artifact-not-verified'
    Add-GateSkipped -Name 'smoke' -Command 'pwsh scripts/smoke.ps1 -ExePath <out>/IshWY.exe -KeepRoot' -Reason $smokeSkipReason
}
else {
    $exePath = Join-Path $outDir 'IshWY.exe'
    $smokeArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'smoke.ps1'), '-ExePath', $exePath, '-KeepRoot')
    $smokeStatus = Invoke-GateStep -Name 'smoke' -FilePath 'pwsh' -Arguments $smokeArgs -LogName 'smoke.log'

    # B7：从 smoke 自己的 SMOKE_ARGV_JSON 标记核对真实 argv 恰含一个 --smoke。
    $smokeArgvJson = Get-LogMarker -LogPath $smokeLogPath -Name 'SMOKE_ARGV_JSON'
    if ($smokeArgvJson) {
        try {
            $smokeArgv = @($smokeArgvJson | ConvertFrom-Json)
            $smokeArgvCount = @($smokeArgv | Where-Object { [string]::Equals([string]$_, '--smoke', [StringComparison]::Ordinal) }).Count
            $smokeArgvVerified = ($smokeArgvCount -eq 1)
        }
        catch {
            $smokeArgvVerified = $false
        }
    }
    else {
        $smokeArgvVerified = $false
    }
    $smokeArgvCommand = 'smoke.ps1 SMOKE_ARGV_JSON 恰含一个 --smoke'
    if ($smokeArgvVerified) {
        Add-GateResult -Name 'smoke-argv' -Command $smokeArgvCommand -ExitCode 0 -Status 'passed' -Reason $null
    }
    else {
        Add-GateResult -Name 'smoke-argv' -Command $smokeArgvCommand -ExitCode 1 -Status 'failed' -Reason 'smoke 启动 argv 未被证明恰含一个 --smoke'
        $script:failed = $true
    }

    $smokeResultPath = Get-LogMarker -LogPath $smokeLogPath -Name 'RESULT_PATH'
    $smokeRoot = Get-LogMarker -LogPath $smokeLogPath -Name 'SMOKE_ROOT'
    if ($smokeResultPath -and (Test-Path -LiteralPath $smokeResultPath -PathType Leaf)) {
        try {
            Copy-Item -LiteralPath $smokeResultPath -Destination $smokeResultEvidence -Force
        }
        catch {
            Write-Warning ("复制 smoke 结果失败：" + $_.Exception.Message)
        }
    }

    # M3：临时根清理失败只警告，不推翻已通过的 smoke。
    if ($smokeStatus -eq 'passed' -and $smokeRoot) {
        $tempBase = ConvertTo-NormalizedPath ([System.IO.Path]::GetTempPath())
        $normalizedSmokeRoot = ConvertTo-NormalizedPath $smokeRoot
        if ($normalizedSmokeRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $smokeRoot -PathType Container)) {
            try {
                Remove-Item -LiteralPath $smokeRoot -Recurse -Force
            }
            catch {
                Write-Warning ("清理 smoke 临时根失败（不影响结果）：" + $_.Exception.Message)
            }
        }
    }
    elseif ($smokeStatus -ne 'passed' -and $smokeRoot) {
        Write-Host ("smoke 失败，保留临时根供排查：" + $smokeRoot) -ForegroundColor Yellow
    }
}

# B8：smoke 成功后重新采样输出；任何增/删/改/读错都失败关闭并阻止新 ZIP。
if ($null -ne $artifactManifestBefore -and $smokeStatus -eq 'passed') {
    $artifactManifestAfter = Get-OutputManifest -Root $outDir
    $outputComparison = Compare-OutputManifests -Before $artifactManifestBefore -After $artifactManifestAfter
    $outputStable = [bool]$artifactManifestAfter.Ok -and [bool]$outputComparison.Ok
    $artifactManifestAfter | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $artifactManifestAfterPath -Encoding utf8
    $outputComparison | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $artifactManifestComparePath -Encoding utf8
    $outputCommand = 'Compare-OutputManifests（smoke 前/后）'
    if ($outputStable) {
        Add-GateResult -Name 'output-stability' -Command $outputCommand -ExitCode 0 -Status 'passed' -Reason $null
    }
    else {
        $outputReasons = @($outputComparison.Reasons)
        if (-not $artifactManifestAfter.Ok) {
            $outputReasons += @($artifactManifestAfter.Reasons | ForEach-Object { 'after:' + $_ })
        }
        Add-GateResult -Name 'output-stability' -Command $outputCommand -ExitCode 1 -Status 'failed' -Reason (($outputReasons | Select-Object -Unique) -join '; ')
        $script:failed = $true
    }
}

# B5 稳定性：HEAD + 指纹 + Dirty 三者都必须不变。
$identityAfter = Get-SourceIdentity -Root $root
$stable = ($identityAfter.Head -eq $identityBefore.Head) -and
    ($identityAfter.Fingerprint -eq $identityBefore.Fingerprint) -and
    ($identityAfter.Dirty -eq $identityBefore.Dirty)
if ($stable) {
    Add-GateResult -Name 'stability' -Command 'git HEAD + source fingerprint + git status --porcelain' -ExitCode 0 -Status 'passed' -Reason $null
}
else {
    Add-GateResult -Name 'stability' -Command 'git HEAD + source fingerprint + git status --porcelain' -ExitCode 1 -Status 'failed' -Reason 'gate 运行期间源码/工作树发生变化'
    $script:failed = $true
}

# B6 ZIP：只有 smoke 通过且源码稳定才生成本次 ZIP。
$zipStatus = 'not-run'
$zipFailed = $false
$zipInfo = [ordered]@{
    createdThisRun = $false
    path = $null
    sha256 = $null
    bytes = 0
    hashVerified = $false
    entryCount = 0
    entryDigest = $null
    entriesVerified = $false
    entriesFile = $null
    entryReasons = @()
    createdAtUtc = $null
    reason = $null
    expectedPath = 'artifacts/' + (Split-Path -Leaf $zipPath)
}
if ($NoZip) {
    $zipStatus = 'skipped'
    $zipInfo.reason = 'skipped-by-request'
    Add-GateSkipped -Name 'zip' -Command 'pwsh scripts/publish.ps1 -ZipOnly' -Reason 'skipped-by-request'
}
elseif ($smokeStatus -ne 'passed') {
    $zipStatus = 'skipped'
    $zipInfo.reason = "smoke-$smokeStatus"
    Add-GateSkipped -Name 'zip' -Command 'pwsh scripts/publish.ps1 -ZipOnly' -Reason $zipInfo.reason
}
elseif (-not $stable) {
    $zipStatus = 'skipped'
    $zipInfo.reason = 'source-unstable'
    Add-GateSkipped -Name 'zip' -Command 'pwsh scripts/publish.ps1 -ZipOnly' -Reason $zipInfo.reason
}
elseif (-not $outputStable) {
    $zipStatus = 'skipped'
    $zipInfo.reason = 'output-unstable'
    Add-GateSkipped -Name 'zip' -Command 'pwsh scripts/publish.ps1 -ZipOnly' -Reason $zipInfo.reason
}
else {
    $zipArgs = @('-NoProfile', '-File', $publishScript, '-ZipOnly', '-Version', $Version, '-Runtime', $Runtime, '-Configuration', 'Release')
    $zipStatus = Invoke-GateStep -Name 'zip' -FilePath 'pwsh' -Arguments $zipArgs -LogName 'zip.log'
    $zipLogPath = Join-Path $logsDir 'zip.log'
    $zipPathReported = Get-LogMarker -LogPath $zipLogPath -Name 'ZIP_PATH'
    $zipShaReported = Get-LogMarker -LogPath $zipLogPath -Name 'ZIP_SHA256'
    $zipBytesReported = Get-LogMarker -LogPath $zipLogPath -Name 'ZIP_BYTES'

    if ($zipStatus -ne 'passed') {
        $zipFailed = $true
        $zipInfo.reason = 'zip-step-failed'
    }
    elseif (-not $zipPathReported) {
        $zipFailed = $true
        $zipInfo.reason = 'zip-path-not-reported'
    }
    elseif ((ConvertTo-NormalizedPath $zipPathReported) -ne (ConvertTo-NormalizedPath $zipPath)) {
        $zipFailed = $true
        $zipInfo.reason = 'zip-path-mismatch'
    }
    elseif (-not (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
        $zipFailed = $true
        $zipInfo.reason = 'zip-missing-after-step'
    }
    else {
        $actualSha = Get-FileSha256 $zipPath
        $zipInfo.hashVerified = ($null -ne $zipShaReported) -and
            [string]::Equals($zipShaReported, $actualSha, [StringComparison]::OrdinalIgnoreCase)
        if (-not $zipInfo.hashVerified) {
            $zipFailed = $true
            $zipInfo.reason = 'zip-hash-mismatch'
        }
        else {
            # B8：独立枚举实际 ZIP 条目并逐文件绑定到 smoke 后的输出清单（不信任发布脚本标记）。
            $zipEntriesPath = Join-Path $hashesDir 'zip-entries.json'
            $zipEntriesComparePath = Join-Path $hashesDir 'zip-entries-compare.json'
            $zipManifest = Get-ZipManifest -ZipPath $zipPath
            $zipEntryCheck = Test-ZipMatchesOutput -ZipManifest $zipManifest -OutputManifest $artifactManifestAfter -RootPrefix (Split-Path -Leaf $outDir)
            $zipManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $zipEntriesPath -Encoding utf8
            $zipEntryCheck | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $zipEntriesComparePath -Encoding utf8
            $zipInfo.entryCount = $zipManifest.FileCount
            $zipInfo.entryDigest = $zipEntryCheck.EntriesDigest
            $zipInfo.entriesFile = 'hashes/zip-entries.json'
            $zipInfo.entryReasons = @($zipEntryCheck.Reasons)
            if (-not $zipEntryCheck.Ok) {
                $zipFailed = $true
                $zipInfo.entriesVerified = $false
                $zipInfo.reason = 'zip-entries-mismatch'
            }
            else {
                $zipInfo.entriesVerified = $true
                $zipInfo.createdThisRun = $true
                $zipInfo.path = 'artifacts/' + (Split-Path -Leaf $zipPath)
                $zipInfo.sha256 = $actualSha
                $zipInfo.bytes = (Get-Item -LiteralPath $zipPath).Length
                $zipInfo.createdAtUtc = (Get-Date).ToUniversalTime().ToString('o')
                $zipInfo.reason = $null
                if ($zipBytesReported -and $zipBytesReported -match '^\d+$' -and [int64]$zipBytesReported -ne $zipInfo.bytes) {
                    Write-Warning ("zip 字节数标记与实际不一致：reported=$zipBytesReported actual=$($zipInfo.bytes)")
                }
            }
        }
    }
    if ($zipFailed) {
        $script:failed = $true
        Write-Host ("ZIP 核验失败：" + $zipInfo.reason) -ForegroundColor Red
    }
}

# 证据文件（B3/B5）。
$sourceHashPath = Join-Path $hashesDir 'source-inputs.sha256'
$sourceHashLines = foreach ($item in $identityBefore.Inputs) {
    '{0}  {1}' -f $item.sha256, $item.path
}
if ($identityBefore.Inputs.Count -gt 0) {
    Set-Content -LiteralPath $sourceHashPath -Value $sourceHashLines -Encoding utf8
}
$excludedPath = Join-Path $evidenceDir 'source-excluded-inputs.txt'
Set-Content -LiteralPath $excludedPath -Encoding utf8 -Value (@($excludedInputs | ForEach-Object { '{0}  {1}' -f $_.reason, $_.path }))
$ignoredPath = Join-Path $evidenceDir 'source-ignored-inputs.txt'
Set-Content -LiteralPath $ignoredPath -Encoding utf8 -Value @($ignoredBuildInputs)
$uncoveredPath = Join-Path $evidenceDir 'source-uncovered-inputs.txt'
Set-Content -LiteralPath $uncoveredPath -Encoding utf8 -Value @($uncoveredInputs)

$corePassed = ($buildStatus -eq 'passed') -and ($testStatus -eq 'passed') -and
    ($formatStatus -eq 'passed') -and ($publishStatus -eq 'passed')

$verdictArgs = @{
    CorePassed = $corePassed
    ArtifactVerified = $artifactVerified
    SourceCovered = $sourceCovered
    Stable = $stable
    SmokeStatus = $smokeStatus
    ZipCreated = [bool]$zipInfo.createdThisRun
    Mode = $Mode
    Dirty = $identityBefore.Dirty
    SkipSmoke = [bool]$SkipSmoke
    NoZip = [bool]$NoZip
    ZipFailed = $zipFailed
    OutputStable = [bool]$outputStable
}
$verdict = Get-GateVerdict @verdictArgs

$unverified = [System.Collections.Generic.List[string]]::new()
if ($SkipSmoke) { $unverified.Add('smoke') }
elseif ($smokeStatus -ne 'passed') { $unverified.Add('smoke') }
if (-not $zipInfo.createdThisRun) { $unverified.Add('zip') }

$notes = [System.Collections.Generic.List[string]]::new()
if ($SkipSmoke) {
    $notes.Add('smoke 未运行（-SkipSmoke）：不能作为发布验收或热键/截图/GUI 通过证据；门禁只能 LIMITED。')
}
if ($NoZip) {
    $notes.Add('未生成 zip（-NoZip）：releaseReady 保持 false；旧 ZIP 不得作为本次证据。')
}
if (-not $sourceCovered) {
    $notes.Add('构建输入覆盖不完整：ignored 构建输入与 uncovered 文件已列入 source-ignored-inputs.txt / source-uncovered-inputs.txt，完整门禁必须失败。')
}
if (-not $stable) {
    $notes.Add('门禁运行期间源码或工作树发生变化：manifest 记录前后两次 HEAD/fingerprint/dirty。')
}
if (-not $outputStable) {
    $notes.Add('smoke 前后输出清单不一致或读取失败（B8）：已阻止本次 ZIP，证据见 hashes/artifact-manifest-*.json 与 hashes/zip-entries*.json。')
}
$notes.Add('B8 证据边界：输出/ZIP 绑定只覆盖本次采样到的普通文件与 ZIP 条目，不防同权限恶意进程的瞬时替换/并发写入，也不提供 OS 沙箱或原子快照。')
if ($smokeStatus -eq 'failed' -and $smokeRoot) {
    $notes.Add('smoke 失败，临时根保留在：' + $smokeRoot)
}
$notes.Add('本 manifest 的 hashes 段记录各证据清单/smoke 原始结果的 SHA256，防止清单被改写。')
$notes.Add('E09 真实样本截图只能由 lead 安全复核放行后采集；本门禁不代采集、不冒充验收。')

$hashRecords = [ordered]@{
    sourceInputs = New-EvidenceFileRecord -EvidenceDir $evidenceDir -RelativePath 'hashes/source-inputs.sha256'
    excludedInputs = New-EvidenceFileRecord -EvidenceDir $evidenceDir -RelativePath 'source-excluded-inputs.txt'
    artifactFiles = New-EvidenceFileRecord -EvidenceDir $evidenceDir -RelativePath 'hashes/artifact-files.sha256'
    artifactManifestBefore = New-EvidenceFileRecord -EvidenceDir $evidenceDir -RelativePath 'hashes/artifact-manifest-before.json'
    artifactManifestAfter = New-EvidenceFileRecord -EvidenceDir $evidenceDir -RelativePath 'hashes/artifact-manifest-after.json'
    artifactManifestCompare = New-EvidenceFileRecord -EvidenceDir $evidenceDir -RelativePath 'hashes/artifact-manifest-compare.json'
    zipEntries = New-EvidenceFileRecord -EvidenceDir $evidenceDir -RelativePath 'hashes/zip-entries.json'
    zipEntriesCompare = New-EvidenceFileRecord -EvidenceDir $evidenceDir -RelativePath 'hashes/zip-entries-compare.json'
    smokeResult = New-EvidenceFileRecord -EvidenceDir $evidenceDir -RelativePath 'smoke/smoke-result.json'
}

$artifactDetail = if ($null -ne $artifactCheck) { $artifactCheck.Detail } else { $null }
$manifest = [ordered]@{
    schemaVersion = 2
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    startedAtUtc = $startedAtUtc.ToString('o')
    mode = $Mode
    version = [ordered]@{
        requested = $Version
        core = $shape.Core
        fileVersion = $shape.FileVersion
        prerelease = $shape.Prerelease
    }
    runtime = $Runtime
    environment = Get-EnvironmentIdentity
    source = [ordered]@{
        root = $root
        head = $identityBefore.Head
        dirty = $identityBefore.Dirty
        dirtyFileCount = $identityBefore.DirtyFileCount
        fingerprint = $identityBefore.Fingerprint
        inputCount = $identityBefore.InputCount
        inputsFile = 'hashes/source-inputs.sha256'
        excludedInputsFile = 'source-excluded-inputs.txt'
        ignoredBuildInputs = @($ignoredBuildInputs)
        uncoveredInputs = @($uncoveredInputs)
        covered = $sourceCovered
        after = [ordered]@{
            head = $identityAfter.Head
            dirty = $identityAfter.Dirty
            dirtyFileCount = $identityAfter.DirtyFileCount
            fingerprint = $identityAfter.Fingerprint
            stable = $stable
        }
    }
    gates = @($gates)
    artifact = [ordered]@{
        outDir = ('artifacts/IshWY-{0}-{1}' -f $Version, $Runtime)
        fileCount = if ($artifactDetail) { $artifactDetail.fileCount } elseif ($null -ne $artifactManifestBefore) { $artifactManifestBefore.FileCount } else { 0 }
        totalBytes = if ($artifactDetail) { $artifactDetail.totalBytes } elseif ($null -ne $artifactManifestBefore) { $artifactManifestBefore.TotalBytes } else { 0 }
        filesFile = if ($null -ne $artifactManifestBefore -and $artifactManifestBefore.FileCount -gt 0) { 'hashes/artifact-files.sha256' } else { $null }
        manifestBeforeFile = if ($null -ne $artifactManifestBefore) { 'hashes/artifact-manifest-before.json' } else { $null }
        manifestAfterFile = if ($null -ne $artifactManifestAfter) { 'hashes/artifact-manifest-after.json' } else { $null }
        manifestCompareFile = if ($null -ne $outputComparison) { 'hashes/artifact-manifest-compare.json' } else { $null }
        stableDuringSmoke = if ($null -ne $outputComparison) { [bool]$outputComparison.Ok } else { $null }
        exeVersion = if ($artifactDetail) { $artifactDetail.exeProductVersion } else { $null }
        dllVersion = if ($artifactDetail) { $artifactDetail.dllProductVersion } else { $null }
        exeVersionSource = if ($artifactDetail) { $artifactDetail.exeVersionSource } else { $null }
        verified = $artifactVerified
        reasons = if ($artifactCheck) { @($artifactCheck.Reasons) } else { @() }
    }
    smoke = [ordered]@{
        status = $smokeStatus
        skippedReason = $smokeSkipReason
        resultFile = if (Test-Path -LiteralPath $smokeResultEvidence -PathType Leaf) { 'smoke/smoke-result.json' } else { $null }
        resultSha256 = if (Test-Path -LiteralPath $smokeResultEvidence -PathType Leaf) { Get-FileSha256 $smokeResultEvidence } else { $null }
        root = $smokeRoot
        argv = @($smokeArgv)
        argvVerified = $smokeArgvVerified
    }
    zip = $zipInfo
    hashes = $hashRecords
    verdict = $verdict.Verdict
    complete = $verdict.Complete
    exitCode = $verdict.ExitCode
    unverified = @($unverified)
    releaseReady = $verdict.ReleaseReady
    hardFailures = @($verdict.HardFailures)
    reasons = @($verdict.Reasons)
    notes = @($notes)
}

$manifestPath = Join-Path $evidenceDir 'manifest.json'
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding utf8

Write-Host ''
Write-Host ("manifest：" + $manifestPath)
foreach ($gate in $gates) {
    $color = switch ($gate.status) {
        'passed' { 'Green' }
        'failed' { 'Red' }
        default { 'Yellow' }
    }
    Write-Host ("  {0,-18} {1}" -f $gate.name, $gate.status) -ForegroundColor $color
}
if ($verdict.Verdict -eq 'passed') {
    Write-Host ('GATE PASS  version=' + $Version + ' runtime=' + $Runtime) -ForegroundColor Green
}
elseif ($verdict.Verdict -eq 'limited') {
    Write-Host ('GATE LIMITED  未验证项：' + (@($unverified) -join ', ') + '；原因：' + (@($verdict.Reasons) -join ', ')) -ForegroundColor Yellow
}
else {
    Write-Host ('GATE FAIL  原因：' + (@($verdict.HardFailures) -join ', ') + '；证据：' + $evidenceDir) -ForegroundColor Red
}

exit $verdict.ExitCode
