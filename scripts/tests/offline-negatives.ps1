#Requires -Version 7.0
<#
P-10/P-11 离线负例：不启动 App、不构建、不打包，只用临时目录与纯函数验证：
1) 冒烟结果校验器拒绝缺失/旧 nonce/错 PID/错路径/错根/缺检查/多余字段/非 true success；
2) 版本规则：严格 SemVer、无前导零、拒绝 shell 字符；workflow 输入规范化只允许一个前导 v；
3) 产物版本精确匹配（1.3.1 不接受 1.3.10，允许 +metadata）；
4) release 模式拒绝 dirty；门禁判决只在完整证据时 passed；
5) 构建输入「默认纳入、显式排除」：NativeMethods.txt/.xml/新后缀纳入，生成物与资料排除；
   被忽略的构建输入列为覆盖缺口；
6) 源码指纹对构建输入变化敏感、对非输入文档变化不敏感、对删除敏感；
7) 唯一证据目录不复用；产物断言在缺失/空目录时失败；
8) 静态契约：CI 不带 -SkipSmoke/-NoZip、release 先校验后写 GITHUB_OUTPUT、gh 命令不插值、
   smoke 先异步读取再 WaitForExit、publish 透传 -p:Version。
任一断言失败即以非 0 退出。
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptsRoot = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptsRoot
. (Join-Path $scriptsRoot 'lib/SmokeResult.ps1')
. (Join-Path $scriptsRoot 'lib/SourceIdentity.ps1')
. (Join-Path $scriptsRoot 'lib/GateSupport.ps1')

$failures = [System.Collections.Generic.List[string]]::new()
$script:checks = 0

function Assert-True {
    param([bool]$Condition, [string]$Name)
    $script:checks++
    if ($Condition) {
        Write-Host ("ok   {0}" -f $Name)
    }
    else {
        $script:failures.Add($Name)
        Write-Host ("FAIL {0}" -f $Name) -ForegroundColor Red
    }
}

function Assert-Throws {
    param([scriptblock]$Action, [string]$Name)
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    Assert-True $threw $Name
}

function Assert-Order {
    param([string]$Text, [string]$First, [string]$Second, [string]$Name)
    $a = $Text.IndexOf($First, [StringComparison]::Ordinal)
    $b = $Text.IndexOf($Second, [StringComparison]::Ordinal)
    Assert-True ($a -ge 0 -and $b -ge 0 -and $a -lt $b) $Name
}

function Read-RepoText {
    param([string]$RelativePath)
    return (Get-Content -LiteralPath (Join-Path $repoRoot $RelativePath) -Raw)
}

function New-TestRoot {
    param([string]$Base)
    $nonce = [Guid]::NewGuid().ToString('N')
    $root = Join-Path $Base (Join-Path 'IshWY-smoke' $nonce)
    New-Item -ItemType Directory -Path $root -Force | Out-Null
    return [pscustomobject]@{ Nonce = $nonce; Root = $root; Exe = (Join-Path $Base 'IshWY.exe') }
}

function Write-SmokeResult {
    param([string]$Root, $Object)
    $json = $Object | ConvertTo-Json -Depth 8
    Set-Content -LiteralPath (Join-Path $Root 'smoke-result.json') -Value $json -Encoding utf8
}

$base = Join-Path ([System.IO.Path]::GetTempPath()) ('IshWY-smoke-negatives-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $base -Force | Out-Null

try {
    # ---- 1. 冒烟结果校验器 ----
    $case = New-TestRoot $base
    $good = [ordered]@{
        schemaVersion = 1
        nonce = $case.Nonce
        processId = 4242
        executablePath = $case.Exe
        dataRoot = $case.Root
        success = $true
        checks = @('xaml-initialized', 'main-window-loaded', 'dispatcher-roundtrip')
        error = $null
    }
    Write-SmokeResult $case.Root $good
    $verdict = Test-SmokeResultFile -Root $case.Root -Nonce $case.Nonce -ProcessId 4242 -ExePath $case.Exe
    Assert-True $verdict.Ok 'positive-control-valid-result-accepted'

    $missing = Test-SmokeResultFile -Root (Join-Path $base 'no-such-root') -Nonce $case.Nonce -ProcessId 4242 -ExePath $case.Exe
    Assert-True (-not $missing.Ok -and $missing.Reasons -contains 'result-missing') 'missing-result-rejected'

    Set-Content -LiteralPath (Join-Path $case.Root 'smoke-result.json') -Value '{not json' -Encoding utf8
    $badJson = Test-SmokeResultFile -Root $case.Root -Nonce $case.Nonce -ProcessId 4242 -ExePath $case.Exe
    Assert-True (-not $badJson.Ok -and $badJson.Reasons -contains 'result-json-invalid') 'invalid-json-rejected'

    $mutations = @(
        @{ Name = 'schema-version'; Mutate = { param($o) $o.schemaVersion = 0 }; Reason = 'result-schema-version' },
        @{ Name = 'nonce'; Mutate = { param($o) $o.nonce = '00000000000000000000000000000000' }; Reason = 'result-nonce' },
        @{ Name = 'pid'; Mutate = { param($o) $o.processId = 1 }; Reason = 'result-process-id' },
        @{ Name = 'exe-path'; Mutate = { param($o) $o.executablePath = (Join-Path $base 'other.exe') }; Reason = 'result-executable-path' },
        @{ Name = 'data-root'; Mutate = { param($o) $o.dataRoot = (Join-Path $base 'other-root') }; Reason = 'result-data-root' },
        @{ Name = 'success-false'; Mutate = { param($o) $o.success = $false }; Reason = 'result-success' },
        @{ Name = 'success-string'; Mutate = { param($o) $o.success = 'true' }; Reason = 'result-success' },
        @{ Name = 'checks-missing-one'; Mutate = { param($o) $o.checks = @('xaml-initialized', 'main-window-loaded') }; Reason = 'result-checks' },
        @{ Name = 'checks-extra'; Mutate = { param($o) $o.checks = @('xaml-initialized', 'main-window-loaded', 'dispatcher-roundtrip', 'extra') }; Reason = 'result-checks' },
        @{ Name = 'checks-duplicate'; Mutate = { param($o) $o.checks = @('xaml-initialized', 'xaml-initialized', 'dispatcher-roundtrip') }; Reason = 'result-checks' },
        @{ Name = 'error-non-null'; Mutate = { param($o) $o.error = 'boom' }; Reason = 'result-error' }
    )

    foreach ($mutation in $mutations) {
        $obj = [ordered]@{}
        foreach ($key in $good.Keys) { $obj[$key] = $good[$key] }
        & $mutation.Mutate $obj
        Write-SmokeResult $case.Root $obj
        $result = Test-SmokeResultFile -Root $case.Root -Nonce $case.Nonce -ProcessId 4242 -ExePath $case.Exe
        Assert-True (-not $result.Ok -and $result.Reasons -contains $mutation.Reason) ("reject-" + $mutation.Name)
    }

    $extra = [ordered]@{}
    foreach ($key in $good.Keys) { $extra[$key] = $good[$key] }
    $extra['unexpected'] = 1
    Write-SmokeResult $case.Root $extra
    $extraResult = Test-SmokeResultFile -Root $case.Root -Nonce $case.Nonce -ProcessId 4242 -ExePath $case.Exe
    Assert-True (-not $extraResult.Ok -and $extraResult.Reasons -contains 'result-fields-mismatch') 'reject-extra-field'

    # ---- 2. 版本规则 ----
    foreach ($version in @('1.3.1', '0.0.0-ci', '1.3.1-beta.1', '1.3.10', '10.0.0-rc.1')) {
        Assert-True (Test-VersionString $version) ("accept-version-" + $version)
    }
    $badVersions = @('', '1.3', 'v1.3.1', '1.3.1 beta', '1.3.1;Remove-Item x', '1.3.1$(whoami)', '../1.3.1', ('1.3.1' + [char]10 + 'rm'), '01.3.1', '1.03.1', '1.3.01', '1.3.1-', '1.3.1+build')
    $index = 0
    foreach ($version in $badVersions) {
        $index++
        Assert-True (-not (Test-VersionString $version)) ("reject-version-case-" + $index)
    }
    Assert-True ((ConvertTo-StrictVersion 'v1.3.1') -eq '1.3.1') 'strict-version-normalizes-single-v'
    Assert-True ((ConvertTo-StrictVersion '1.3.1-beta.1') -eq '1.3.1-beta.1') 'strict-version-keeps-prerelease'
    Assert-Throws { ConvertTo-StrictVersion 'vv1.3.1' } 'strict-version-rejects-double-v'
    Assert-Throws { ConvertTo-StrictVersion ' 1.3.1' } 'strict-version-rejects-leading-space'
    Assert-Throws { ConvertTo-StrictVersion ('1.3.1' + [char]10 + 'echo pwned') } 'strict-version-rejects-newline'
    Assert-Throws { ConvertTo-StrictVersion '1.3.1;Remove-Item x' } 'strict-version-rejects-semicolon'
    Assert-Throws { ConvertTo-StrictVersion 'v1.3.1;echo' } 'strict-version-rejects-normalized-but-dirty'

    $shape = Get-VersionShape '1.3.1-beta.1'
    Assert-True ($shape.Core -eq '1.3.1') 'version-shape-core'
    Assert-True ($shape.FileVersion -eq '1.3.1.0') 'version-shape-file-version'
    Assert-True ($shape.Prerelease -eq 'beta.1') 'version-shape-prerelease'

    # ---- 3. 产物版本精确匹配 ----
    $okMatch = Test-ArtifactVersionMatch -Expected '1.3.1' -ProductVersion '1.3.1+abcdef' -FileVersion '1.3.1.0'
    Assert-True $okMatch.Ok 'artifact-version-accepts-metadata-suffix'
    $prefixTrap = Test-ArtifactVersionMatch -Expected '1.3.1' -ProductVersion '1.3.10' -FileVersion '1.3.10.0'
    Assert-True (-not $prefixTrap.Ok) 'artifact-version-rejects-1.3.10-for-1.3.1'
    $fileMismatch = Test-ArtifactVersionMatch -Expected '1.3.1' -ProductVersion '1.3.1' -FileVersion '1.3.1'
    Assert-True (-not $fileMismatch.Ok) 'artifact-version-rejects-3-part-file-version'
    Assert-True (@($fileMismatch.Reasons | Where-Object { $_ -like 'file-version-mismatch*' }).Count -eq 1) 'artifact-version-file-mismatch-reason'
    $missingProduct = Test-ArtifactVersionMatch -Expected '1.3.1' -ProductVersion '' -FileVersion '1.3.1.0'
    Assert-True (-not $missingProduct.Ok -and $missingProduct.Reasons -contains 'product-version-missing') 'artifact-version-rejects-missing-product'
    $prereleaseMatch = Test-ArtifactVersionMatch -Expected '1.3.1-beta.1' -ProductVersion '1.3.1-beta.1' -FileVersion '1.3.1.0'
    Assert-True $prereleaseMatch.Ok 'artifact-version-accepts-prerelease'

    # ---- 4. release 源码策略 ----
    Assert-True ((Test-ReleaseSourceAllowed -Mode 'release' -Dirty $true) -eq 'release-refuses-dirty-source') 'release-dirty-refused'
    Assert-True ($null -eq (Test-ReleaseSourceAllowed -Mode 'release' -Dirty $false)) 'release-clean-allowed'
    Assert-True ($null -eq (Test-ReleaseSourceAllowed -Mode 'ci' -Dirty $true)) 'ci-dirty-allowed'

    # ---- 5. 构建输入覆盖规则 ----
    foreach ($included in @(
            'src/PrtScr.App/NativeMethods.txt',
            'src/PrtScr.App/app.manifest',
            'src/PrtScr.App/PrtScr.App.csproj',
            'src/PrtScr.Core/NewFile.razor',
            'scripts/gate.ps1',
            '.github/workflows/ci.yml',
            'Directory.Build.props',
            'global.json')) {
        Assert-True (Test-BuildInputPath $included) ("build-input-included-" + ($included -replace '[/.]', '-'))
    }
    foreach ($excluded in @(
            'src/PrtScr.App/bin/Debug/IshWY.dll',
            'src/PrtScr.App/obj/project.assets.json',
            'artifacts/IshWY-1.3.1-win-x64/IshWY.exe',
            '.tmp/scratch.txt',
            'node_modules/pkg/index.js',
            'publish/IshWY.exe',
            'data/settings.json',
            'Record/HeadMaid.md',
            'docs/readme.md',
            'README.md',
            'LICENSE',
            '.gitignore',
            '.gitattributes')) {
        Assert-True (-not (Test-BuildInputPath $excluded)) ("build-input-excluded-" + ($excluded -replace '[/.]', '-'))
    }
    Assert-True (Test-IgnoredBuildInputCandidate 'src/generated/NativeMethods.txt') 'ignored-src-file-is-coverage-gap'
    Assert-True (Test-IgnoredBuildInputCandidate 'Directory.Build.props') 'ignored-props-is-coverage-gap'
    Assert-True (-not (Test-IgnoredBuildInputCandidate 'screenshot.png')) 'ignored-screenshot-not-coverage-gap'
    Assert-True (-not (Test-IgnoredBuildInputCandidate 'clip_probe.png')) 'ignored-clip-probe-not-coverage-gap'

    # ---- 6. 源码指纹 fixture ----
    $fixture = Join-Path $base 'fingerprint-fixture'
    New-Item -ItemType Directory -Path (Join-Path $fixture 'src') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $fixture 'docs') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fixture 'src/a.cs') -Value 'class A {}' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $fixture 'docs/readme.md') -Value 'doc' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $fixture '.gitignore') -Value @('src/generated/', '*.log') -Encoding utf8
    & git -C $fixture init -q
    if ($LASTEXITCODE -ne 0) { throw 'git init 失败，无法验证源码指纹' }

    $first = Get-SourceFingerprint -Root $fixture
    Assert-True (@($first.Inputs).Count -eq 1) 'fingerprint-includes-src-only'
    Assert-True ($first.Fingerprint.Length -eq 64) 'fingerprint-sha256-length'

    Set-Content -LiteralPath (Join-Path $fixture 'src/a.cs') -Value 'class A { int x; }' -Encoding utf8
    $second = Get-SourceFingerprint -Root $fixture
    Assert-True ($second.Fingerprint -ne $first.Fingerprint) 'fingerprint-sensitive-to-src-change'

    Set-Content -LiteralPath (Join-Path $fixture 'docs/readme.md') -Value 'doc changed' -Encoding utf8
    $third = Get-SourceFingerprint -Root $fixture
    Assert-True ($third.Fingerprint -eq $second.Fingerprint) 'fingerprint-ignores-docs-change'

    Set-Content -LiteralPath (Join-Path $fixture 'src/new.cs') -Value 'class B {}' -Encoding utf8
    $fourth = Get-SourceFingerprint -Root $fixture
    Assert-True ($fourth.Fingerprint -ne $third.Fingerprint) 'fingerprint-sensitive-to-new-src-file'

    Set-Content -LiteralPath (Join-Path $fixture 'src/NativeMethods.txt') -Value 'user32.dll' -Encoding utf8
    $fifth = Get-SourceFingerprint -Root $fixture
    Assert-True (@($fifth.Inputs | Where-Object { $_.path -eq 'src/NativeMethods.txt' }).Count -eq 1) 'fingerprint-includes-nativemethods-txt'
    Assert-True ($fifth.Fingerprint -ne $fourth.Fingerprint) 'fingerprint-sensitive-to-txt-input'

    Set-Content -LiteralPath (Join-Path $fixture 'src/App.manifest') -Value '<assembly/>' -Encoding utf8
    $sixth = Get-SourceFingerprint -Root $fixture
    Assert-True (@($sixth.Inputs | Where-Object { $_.path -eq 'src/App.manifest' }).Count -eq 1) 'fingerprint-includes-manifest-xml'

    Remove-Item -LiteralPath (Join-Path $fixture 'src/new.cs') -Force
    $seventh = Get-SourceFingerprint -Root $fixture
    Assert-True ($seventh.Fingerprint -ne $sixth.Fingerprint) 'fingerprint-sensitive-to-deletion'

    New-Item -ItemType Directory -Path (Join-Path $fixture 'src/generated') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $fixture 'src/generated/NativeMethods.txt') -Value 'ignored input' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $fixture 'build.log') -Value 'log' -Encoding utf8
    $eighth = Get-SourceFingerprint -Root $fixture
    Assert-True (@($eighth.IgnoredInputCandidates) -contains 'src/generated/NativeMethods.txt') 'ignored-build-input-listed-as-gap'
    Assert-True (@($eighth.IgnoredInputCandidates) -notcontains 'build.log') 'ignored-log-not-a-gap'

    # ---- 7. 唯一证据目录与产物断言 ----
    $evidenceRoot = Join-Path $base 'evidence'
    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
    $ev1 = New-EvidenceDirectory -EvidenceRoot $evidenceRoot -Mode 'ci'
    Start-Sleep -Milliseconds 5
    $ev2 = New-EvidenceDirectory -EvidenceRoot $evidenceRoot -Mode 'ci'
    Assert-True ($ev1 -ne $ev2) 'evidence-directories-unique'
    Assert-True ((Test-Path -LiteralPath $ev1) -and (Test-Path -LiteralPath $ev2)) 'evidence-directories-created'
    $null = New-EvidenceDirectory -EvidenceRoot $evidenceRoot -Mode 'ci' -Timestamp '20260101-000000-000' -Suffix 'deadbeef'
    Assert-Throws { New-EvidenceDirectory -EvidenceRoot $evidenceRoot -Mode 'ci' -Timestamp '20260101-000000-000' -Suffix 'deadbeef' } 'evidence-refuses-existing-directory'

    $missingArtifact = Test-PublishArtifact -OutDir (Join-Path $base 'no-outdir') -Version '1.3.1'
    Assert-True (-not $missingArtifact.Ok -and $missingArtifact.Reasons -contains 'outdir-missing') 'artifact-missing-outdir-fails'

    $emptyOutDir = Join-Path $base 'empty-outdir'
    New-Item -ItemType Directory -Path $emptyOutDir -Force | Out-Null
    $emptyArtifact = Test-PublishArtifact -OutDir $emptyOutDir -Version '1.3.1'
    Assert-True (-not $emptyArtifact.Ok -and $emptyArtifact.Reasons -contains 'artifact-empty') 'artifact-empty-outdir-fails'
    Assert-True ($emptyArtifact.Reasons -contains 'exe-missing') 'artifact-empty-reports-exe-missing'
    Assert-True ($emptyArtifact.Reasons -contains 'dll-missing') 'artifact-empty-reports-dll-missing'
    Assert-True ($emptyArtifact.Reasons -contains 'pri-missing') 'artifact-empty-reports-pri-missing'

    # ---- 8. 门禁判决 ----
    $passed = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $true -SmokeStatus 'passed' -ZipCreated $true -Mode 'ci' -Dirty $true -OutputStable $true
    Assert-True ($passed.Verdict -eq 'passed' -and $passed.ExitCode -eq 0) 'verdict-passed-exit-0'
    $skipSmokePassed = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $true -SmokeStatus 'passed' -ZipCreated $true -Mode 'ci' -Dirty $true -SkipSmoke $true -OutputStable $true
    Assert-True (-not $skipSmokePassed.Complete) 'verdict-skip-smoke-never-complete'

    $skipSmoke = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $true -SmokeStatus 'not-run' -ZipCreated $false -Mode 'ci' -Dirty $true -SkipSmoke $true -OutputStable $true
    Assert-True ($skipSmoke.Verdict -eq 'limited' -and $skipSmoke.ExitCode -eq 2) 'verdict-skip-smoke-limited-2'
    Assert-True ($skipSmoke.Reasons -contains 'smoke-skipped-by-request') 'verdict-skip-smoke-reason'

    $noZip = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $true -SmokeStatus 'passed' -ZipCreated $false -Mode 'ci' -Dirty $true -NoZip $true -OutputStable $true
    Assert-True ($noZip.Verdict -eq 'limited' -and $noZip.ExitCode -eq 2 -and $noZip.Reasons -contains 'zip-skipped-by-request') 'verdict-no-zip-limited-2'

    $unstable = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $false -SmokeStatus 'passed' -ZipCreated $true -Mode 'ci' -Dirty $true -OutputStable $true
    Assert-True ($unstable.Verdict -eq 'failed' -and $unstable.ExitCode -eq 1 -and $unstable.HardFailures -contains 'source-changed-during-gate') 'verdict-unstable-failed'

    $unverifiedArtifact = Get-GateVerdict -CorePassed $true -ArtifactVerified $false -SourceCovered $true -Stable $true -SmokeStatus 'not-run' -ZipCreated $false -Mode 'ci' -Dirty $true -OutputStable $true
    Assert-True ($unverifiedArtifact.Verdict -eq 'failed' -and $unverifiedArtifact.HardFailures -contains 'artifact-not-verified') 'verdict-unverified-artifact-failed'

    $coverageGap = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $false -Stable $true -SmokeStatus 'passed' -ZipCreated $true -Mode 'ci' -Dirty $true -OutputStable $true
    Assert-True ($coverageGap.Verdict -eq 'failed' -and $coverageGap.HardFailures -contains 'source-input-coverage-gap') 'verdict-coverage-gap-failed'

    $zipFailed = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $true -SmokeStatus 'passed' -ZipCreated $false -Mode 'ci' -Dirty $true -ZipFailed $true -OutputStable $true
    Assert-True ($zipFailed.Verdict -eq 'failed' -and $zipFailed.HardFailures -contains 'zip-failed') 'verdict-zip-failed-hard'

    $smokeFailed = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $true -SmokeStatus 'failed' -ZipCreated $false -Mode 'ci' -Dirty $true -OutputStable $true
    Assert-True ($smokeFailed.Verdict -eq 'failed' -and $smokeFailed.HardFailures -contains 'smoke-failed') 'verdict-smoke-failed'

    $releaseClean = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $true -SmokeStatus 'passed' -ZipCreated $true -Mode 'release' -Dirty $false -OutputStable $true
    Assert-True $releaseClean.ReleaseReady 'verdict-release-clean-ready'
    $releaseDirty = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $true -SmokeStatus 'passed' -ZipCreated $true -Mode 'release' -Dirty $true -OutputStable $true
    Assert-True (-not $releaseDirty.ReleaseReady) 'verdict-release-dirty-not-ready'

    # ---- 9. 静态契约（防止有人把严格语义改回去） ----
    $ciText = Read-RepoText '.github/workflows/ci.yml'
    $releaseText = Read-RepoText '.github/workflows/release.yml'
    $gateText = Read-RepoText 'scripts/gate.ps1'
    $publishText = Read-RepoText 'scripts/publish.ps1'
    $smokeText = Read-RepoText 'scripts/smoke.ps1'

    Assert-True ($ciText.Contains('scripts/gate.ps1 -Mode ci')) 'ci-invokes-gate'
    $ciCodeLines = @([regex]::Split($ciText, '\r?\n') | Where-Object { $_.TrimStart() -notlike '#*' })
    Assert-True (-not (@($ciCodeLines | Where-Object { $_.Contains('-SkipSmoke') }).Count -gt 0)) 'ci-has-no-skipsmoke'
    Assert-True (-not (@($ciCodeLines | Where-Object { $_.Contains('-NoZip') }).Count -gt 0)) 'ci-has-no-nozip'
    Assert-True ($ciText.Contains('if: always()')) 'ci-uploads-evidence-always'

    Assert-True ($releaseText.Contains('EVENT_NAME: ${{ github.event_name }}')) 'release-passes-event-name-via-env'
    Assert-True ($releaseText.Contains('INPUT_VERSION: ${{ inputs.version }}')) 'release-passes-input-via-env'
    Assert-True ($releaseText.Contains('ConvertTo-StrictVersion')) 'release-uses-strict-version'
    Assert-Order $releaseText 'ConvertTo-StrictVersion' 'Out-File -FilePath $env:GITHUB_OUTPUT' 'release-validates-before-writing-output'
    Assert-True ($releaseText.Contains('git check-ref-format')) 'release-validates-tag-ref'
    $ghLines = @([regex]::Split($releaseText, '\r?\n') | Where-Object { $_.Contains('gh release create') })
    Assert-True ($ghLines.Count -ge 1) 'release-has-gh-release-create'
    $ghInterpolated = @($ghLines | Where-Object { $_.Contains('${{') })
    Assert-True ($ghInterpolated.Count -eq 0) 'release-gh-command-has-no-interpolation'
    Assert-True (([regex]::Matches($releaseText, 'if: always\(\)')).Count -ge 1) 'release-uploads-evidence-always'

    Assert-True ($gateText.Contains('New-EvidenceDirectory')) 'gate-uses-unique-evidence-directory'
    Assert-True ($gateText.Contains("'-SkipSmoke', '-NoZip'")) 'gate-stages-without-zip'
    Assert-True ($gateText.Contains('-ZipOnly')) 'gate-zips-via-ziponly-after-smoke'
    Assert-True ($gateText.Contains('Get-GateVerdict')) 'gate-uses-verdict-helper'
    Assert-True ($gateText.Contains('createdThisRun')) 'gate-tracks-zip-created-this-run'
    Assert-Order $gateText '$smokeStatus = Invoke-GateStep' "'-ZipOnly'" 'gate-smoke-before-zip'
    Assert-True ($gateText.Contains('$identityAfter.Dirty -eq $identityBefore.Dirty')) 'gate-compares-dirty'
    Assert-True ($gateText.Contains('GATE LIMITED')) 'gate-has-limited-verdict'

    Assert-True ($publishText.Contains('-p:Version=')) 'publish-passes-version-property'
    Assert-True ($publishText.Contains('-p:FileVersion=')) 'publish-passes-file-version-property'
    Assert-True ($publishText.Contains('-ZipOnly')) 'publish-supports-ziponly'
    Assert-True ($publishText.Contains('ZIP_SHA256=')) 'publish-reports-zip-hash'

    Assert-True ($smokeText.Contains('ReadToEndAsync')) 'smoke-reads-asynchronously'
    Assert-Order $smokeText 'ReadToEndAsync' 'WaitForExit(' 'smoke-reads-before-waiting'
    Assert-True ($smokeText.Contains('stdout-read-incomplete')) 'smoke-records-read-failure'
    Assert-True ($smokeText.Contains('KeepRoot')) 'smoke-supports-keeproot'
}
finally {
    if (Test-Path -LiteralPath $base) {
        Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failures.Count -gt 0) {
    Write-Host ("==> OFFLINE NEGATIVES FAIL：{0}/{1}" -f $failures.Count, $checks) -ForegroundColor Red
    $failures | ForEach-Object { Write-Host ("    - " + $_) }
    exit 1
}

Write-Host ("==> OFFLINE NEGATIVES PASS：{0}/{0}" -f $checks) -ForegroundColor Green
exit 0