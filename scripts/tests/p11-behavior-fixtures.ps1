#Requires -Version 7.0
<#
P11 第二轮行为夹具（B7/B8/B9/M2/M3）。
只用离线冒烟替身、临时目录与合成 ZIP：
- 不启动真实 IshWY、不 dotnet build/publish、不打包真实产物、不按名称杀用户进程；
- B7 通过 pwsh -EncodedCommand 传入数组参数（-File 绑定数组会退化成首元素）；
- 检查计数与退出码单独输出，供报告引用。
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).ProviderPath
$smokeScript = Join-Path $repoRoot 'scripts\smoke.ps1'
$stubScript = Join-Path $repoRoot 'scripts\tests\fixtures\smoke-stub.ps1'
$gateScript = Join-Path $repoRoot 'scripts\gate.ps1'
$pwshPath = (Get-Process -Id $PID).Path

. (Join-Path $repoRoot 'scripts\lib\SmokeResult.ps1')
. (Join-Path $repoRoot 'scripts\lib\SourceIdentity.ps1')
. (Join-Path $repoRoot 'scripts\lib\GateSupport.ps1')

$checks = 0
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-True {
    param([bool]$Condition, [string]$Name)
    $script:checks++
    if ($Condition) {
        Write-Host ('ok   ' + $Name)
    }
    else {
        $script:failures.Add($Name)
        Write-Host ('FAIL ' + $Name) -ForegroundColor Red
    }
}

function Assert-Eq {
    param($Expected, $Actual, [string]$Name)
    Assert-True ([string]$Expected -ceq [string]$Actual) ($Name + ' [exp=' + [string]$Expected + ' act=' + [string]$Actual + ']')
}

function Assert-Contains {
    param([string]$Text, [string]$Needle, [string]$Name)
    $hit = $Text.IndexOf($Needle, [StringComparison]::Ordinal) -ge 0
    Assert-True $hit ($Name + ' [needle=' + $Needle + ']')
}

function Invoke-Captured {
    param(
        [string]$FilePath,
        [string[]]$Arguments = @(),
        $Environment = $null
    )
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add([string]$argument)
    }
    if ($null -ne $Environment) {
        foreach ($key in $Environment.Keys) {
            $startInfo.Environment[[string]$key] = [string]$Environment[$key]
        }
    }
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout = $stdoutTask.Result
    $stderr = $stderrTask.Result
    $exitCode = $process.ExitCode
    $process.Dispose()
    return [pscustomobject]@{ ExitCode = $exitCode; Stdout = $stdout; Stderr = $stderr }
}

function Invoke-Smoke {
    param(
        [string]$ScriptPath,
        [string[]]$ProcessArguments = @(),
        [int]$TimeoutSeconds = 30,
        [switch]$KeepRoot
    )
    $literal = "@(" + (($ProcessArguments | ForEach-Object { "'" + ([string]$_).Replace("'", "''") + "'" }) -join ',') + ")"
    $command = "& '" + $ScriptPath.Replace("'", "''") + "' -ExePath '" + $pwshPath.Replace("'", "''") + "' -TimeoutSeconds $TimeoutSeconds -ProcessArguments $literal"
    if ($KeepRoot) { $command = $command + ' -KeepRoot' }
    $encoded = [Convert]::ToBase64String([System.Text.Encoding]::Unicode.GetBytes($command))
    return Invoke-Captured -FilePath $pwshPath -Arguments @('-NoProfile', '-EncodedCommand', $encoded)
}

function Get-Marker {
    param([string]$Text, [string]$Name)
    $prefix = $Name + '='
    foreach ($line in $Text.Split([char]10)) {
        $clean = $line.TrimEnd([char]13)
        if ($clean.StartsWith($prefix, [StringComparison]::Ordinal)) {
            return $clean.Substring($prefix.Length)
        }
    }
    return $null
}

function New-TestZip {
    param([string]$Path, $Entries)
    if (Test-Path -LiteralPath $Path) { Remove-Item -LiteralPath $Path -Force }
    $fileStream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($fileStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($entry in $Entries) {
                $zipEntry = $archive.CreateEntry([string]$entry.name)
                if ($null -ne $entry.content) {
                    $bytes = [System.Text.Encoding]::UTF8.GetBytes([string]$entry.content)
                    $stream = $zipEntry.Open()
                    try { $stream.Write($bytes, 0, $bytes.Length) }
                    finally { $stream.Dispose() }
                }
            }
        }
        finally { $archive.Dispose() }
    }
    finally { $fileStream.Dispose() }
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('IshWY-p11b-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
Write-Host ('测试根：' + $testRoot)
Write-Host ''
Write-Host '== B7：真实 exactly-one --smoke argv/env 行为 =='

$baseArguments = @('-NoProfile', '-File', $stubScript)
$positive = Invoke-Smoke -ScriptPath $smokeScript -ProcessArguments $baseArguments -KeepRoot
Assert-Eq 0 $positive.ExitCode 'B7-positive-exit-0'
$combined = $positive.Stdout + [Environment]::NewLine + $positive.Stderr
Assert-Contains $positive.Stdout '==> SMOKE PASS' 'B7-positive-pass-line'
$argvJson = Get-Marker $combined 'SMOKE_ARGV_JSON'
Assert-True ($null -ne $argvJson) 'B7-argv-marker-present'
$parsedArgv = @($argvJson | ConvertFrom-Json)
Assert-Eq 4 $parsedArgv.Count 'B7-argv-total-4'
Assert-Eq (($baseArguments + '--smoke') -join '|') ($parsedArgv -join '|') 'B7-argv-order-and-content'
Assert-Eq 1 (@($parsedArgv | Where-Object { [string]::Equals([string]$_, '--smoke', [StringComparison]::Ordinal) }).Count) 'B7-argv-exactly-one-mode'
Assert-Contains $positive.Stdout $stubScript 'B7-log-shows-real-child-argv'
$positiveRoot = Get-Marker $positive.Stdout 'SMOKE_ROOT'
Assert-True ($null -ne $positiveRoot -and (Test-Path -LiteralPath $positiveRoot -PathType Container)) 'B7-root-marker-and-dir'
Assert-True ($null -ne (Get-Marker $positive.Stdout 'RESULT_PATH')) 'B7-result-marker-present'
$capturePath = Join-Path $positiveRoot 'argv-capture.json'
Assert-True (Test-Path -LiteralPath $capturePath -PathType Leaf) 'B7-child-capture-written'
$capture = Get-Content -LiteralPath $capturePath -Raw -Encoding utf8 | ConvertFrom-Json
Assert-Eq 1 $capture.modeCount 'B7-child-received-exactly-one-mode'
Assert-Eq '--smoke' (@($capture.argv) -join '|') 'B7-child-argv-is-mode-only'
Assert-Eq ([System.IO.Path]::TrimEndingDirectorySeparator($positiveRoot)) ([System.IO.Path]::TrimEndingDirectorySeparator([string]$capture.envRoot)) 'B7-child-env-root-matches'
Assert-Eq (Split-Path -Leaf $positiveRoot) ([string]$capture.envNonce) 'B7-child-env-nonce-matches'
Assert-True ([int]$capture.pid -gt 0) 'B7-child-pid-recorded'
Assert-True ([string]::Equals([System.IO.Path]::GetFullPath([string]$capture.processPath), [System.IO.Path]::GetFullPath($pwshPath), [StringComparison]::OrdinalIgnoreCase)) 'B7-child-process-path'
Remove-Item -LiteralPath $positiveRoot -Recurse -Force

$duplicate = Invoke-Smoke -ScriptPath $smokeScript -ProcessArguments ($baseArguments + '--smoke')
$duplicateText = $duplicate.Stdout + [Environment]::NewLine + $duplicate.Stderr
Assert-True ($duplicate.ExitCode -ne 0) 'B7-duplicate-rejected-nonzero'
# 子进程 stderr 的编码取决于其控制台代码页（本机为 GBK），故拒绝理由用同进程 catch 断言原文，
# 外部真实进程只负责证明退出码非 0 且未创建冒烟根。
$duplicateMessage = ''
try { & $smokeScript -ExePath $pwshPath -ProcessArguments @('--smoke') }
catch { $duplicateMessage = $_.Exception.Message }
Assert-Contains $duplicateMessage '不得包含' 'B7-duplicate-explicit-reason'
Assert-True ($null -eq (Get-Marker $duplicateText 'SMOKE_ROOT')) 'B7-duplicate-rejected-before-launch'

$noResult = Invoke-Smoke -ScriptPath $smokeScript -ProcessArguments ($baseArguments + 'no-result') -KeepRoot
$noResultText = $noResult.Stdout + [Environment]::NewLine + $noResult.Stderr
Assert-Eq 1 $noResult.ExitCode 'B7-exit0-without-result-rejected'
Assert-Contains $noResultText 'result-missing' 'B7-noresult-reason'
$noResultRoot = Get-Marker $noResultText 'SMOKE_ROOT'
if ($null -ne $noResultRoot -and (Test-Path -LiteralPath $noResultRoot)) { Remove-Item -LiteralPath $noResultRoot -Recurse -Force }

$envRoot = Join-Path $testRoot 'env-only-root'
New-Item -ItemType Directory -Path $envRoot -Force | Out-Null
$envOnly = Invoke-Captured -FilePath $pwshPath -Arguments @('-NoProfile', '-File', $stubScript) -Environment @{ ISHWY_SMOKE_ROOT = $envRoot; ISHWY_SMOKE_NONCE = 'env-only-nonce' }
Assert-Eq 0 $envOnly.ExitCode 'B7-envonly-stub-exit-0'
Assert-True (Test-Path -LiteralPath (Join-Path $envRoot 'argv-capture.json') -PathType Leaf) 'B7-envonly-capture-exists'
Assert-True (-not (Test-Path -LiteralPath (Join-Path $envRoot 'smoke-result.json'))) 'B7-envonly-no-result-file'
$envVerdict = Test-SmokeResultFile -Root $envRoot -Nonce 'env-only-nonce' -ProcessId 4242 -ExePath $pwshPath
Assert-Eq 'result-missing' (@($envVerdict.Reasons) -join '|') 'B7-envonly-rejected-as-missing'

$mutatedRoot = Join-Path $testRoot 'mutated-scripts'
New-Item -ItemType Directory -Path (Join-Path $mutatedRoot 'scripts\lib') -Force | Out-Null
Copy-Item -LiteralPath $smokeScript -Destination (Join-Path $mutatedRoot 'scripts\smoke.ps1')
Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\lib\SmokeResult.ps1') -Destination (Join-Path $mutatedRoot 'scripts\lib\SmokeResult.ps1')
$mutatedSmokePath = Join-Path $mutatedRoot 'scripts\smoke.ps1'
$originalText = Get-Content -LiteralPath $mutatedSmokePath -Raw -Encoding utf8
$appendNeedle = '[void]$effectiveArguments.Add($modeArgument)'
Assert-True ($originalText.Contains($appendNeedle, [StringComparison]::Ordinal)) 'B7-mutation-needle-present'
$mutatedText = $originalText.Replace($appendNeedle, '# MUTATED：移除唯一 --smoke 追加点')
[System.IO.File]::WriteAllText($mutatedSmokePath, $mutatedText, [System.Text.UTF8Encoding]::new($false))
$mutatedRun = Invoke-Smoke -ScriptPath $mutatedSmokePath -ProcessArguments $baseArguments
$mutatedRunText = $mutatedRun.Stdout + [Environment]::NewLine + $mutatedRun.Stderr
Assert-True ($mutatedRun.ExitCode -ne 0) 'B7-mutation-append-removed-fails'
$mutatedMessage = ''
try { & $mutatedSmokePath -ExePath $pwshPath -ProcessArguments $baseArguments }
catch { $mutatedMessage = $_.Exception.Message }
Assert-Contains $mutatedMessage '不恰含一个' 'B7-mutation-fails-for-missing-mode'

$smokeText = Get-Content -LiteralPath $smokeScript -Raw -Encoding utf8
Assert-Contains $smokeText 'SMOKE_ARGV_JSON' 'B7-static-argv-marker'
Assert-Contains $smokeText '不恰含一个' 'B7-static-count-guard'
$gateText = Get-Content -LiteralPath $gateScript -Raw -Encoding utf8
Assert-Contains $gateText 'SMOKE_ARGV_JSON' 'B7-gate-reads-argv-marker'
Assert-Contains $gateText 'smoke-argv' 'B7-gate-argv-gate-result'

Write-Host ''
Write-Host '== B8：冒烟前后输出清单与 ZIP 条目逐文件绑定 =='

$outBase = Join-Path $testRoot 'out-base'
New-Item -ItemType Directory -Path (Join-Path $outBase 'sub') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $outBase 'emptydir') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $outBase 'IshWY.exe') -Value 'exe-bytes' -NoNewline -Encoding utf8
Set-Content -LiteralPath (Join-Path $outBase 'IshWY.pri') -Value 'pri-bytes' -NoNewline -Encoding utf8
Set-Content -LiteralPath (Join-Path $outBase 'sub\a.dll') -Value 'dll-bytes' -NoNewline -Encoding utf8

$outManifest = Get-OutputManifest -Root $outBase
Assert-True ([bool]$outManifest.Ok) 'B8-manifest-ok'
Assert-Eq 3 $outManifest.FileCount 'B8-manifest-file-count'
Assert-Eq 2 $outManifest.DirectoryCount 'B8-manifest-directory-count'
Assert-Eq 64 ([string]$outManifest.Digest).Length 'B8-manifest-digest-length'
$outManifestAgain = Get-OutputManifest -Root $outBase
$equalCompare = Compare-OutputManifests -Before $outManifest -After $outManifestAgain
Assert-True ([bool]$equalCompare.Ok) 'B8-compare-equal-ok'
Assert-Eq $outManifest.Digest $equalCompare.AfterDigest 'B8-compare-equal-digest'

$mutDir = Join-Path $testRoot 'out-mut'
New-Item -ItemType Directory -Path (Join-Path $mutDir 'sub') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $mutDir 'IshWY.exe') -Value 'exe-bytes' -NoNewline -Encoding utf8
Set-Content -LiteralPath (Join-Path $mutDir 'sub\a.dll') -Value 'dll-bytes' -NoNewline -Encoding utf8
$mutBase = Get-OutputManifest -Root $mutDir
Set-Content -LiteralPath (Join-Path $mutDir 'sub\a.dll') -Value 'dll-bytes-changed' -NoNewline -Encoding utf8
$changedCompare = Compare-OutputManifests -Before $mutBase -After (Get-OutputManifest -Root $mutDir)
Assert-True (-not [bool]$changedCompare.Ok) 'B8-compare-content-change-fails'
Assert-Eq 'sub/a.dll' (@($changedCompare.Changed) -join '|') 'B8-compare-content-change-item'
Assert-Contains (@($changedCompare.Reasons) -join '|') 'file-changed:sub/a.dll' 'B8-compare-content-change-reason'
Set-Content -LiteralPath (Join-Path $mutDir 'sub\a.dll') -Value 'dll-bytes' -NoNewline -Encoding utf8

Set-Content -LiteralPath (Join-Path $mutDir 'new.dll') -Value 'new' -NoNewline -Encoding utf8
$addedCompare = Compare-OutputManifests -Before $mutBase -After (Get-OutputManifest -Root $mutDir)
Assert-True (-not [bool]$addedCompare.Ok) 'B8-compare-added-fails'
Assert-Eq 'new.dll' (@($addedCompare.Added) -join '|') 'B8-compare-added-item'
Remove-Item -LiteralPath (Join-Path $mutDir 'new.dll') -Force

Remove-Item -LiteralPath (Join-Path $mutDir 'sub\a.dll') -Force
$removedCompare = Compare-OutputManifests -Before $mutBase -After (Get-OutputManifest -Root $mutDir)
Assert-True (-not [bool]$removedCompare.Ok) 'B8-compare-removed-fails'
Assert-Eq 'sub/a.dll' (@($removedCompare.Removed) -join '|') 'B8-compare-removed-item'
Set-Content -LiteralPath (Join-Path $mutDir 'sub\a.dll') -Value 'dll-bytes' -NoNewline -Encoding utf8

New-Item -ItemType Directory -Path (Join-Path $mutDir 'extra-dir') -Force | Out-Null
$dirAddedCompare = Compare-OutputManifests -Before $mutBase -After (Get-OutputManifest -Root $mutDir)
Assert-True (-not [bool]$dirAddedCompare.Ok) 'B8-compare-directory-added-fails'
Assert-Eq 'extra-dir' (@($dirAddedCompare.AddedDirectories) -join '|') 'B8-compare-directory-added-item'
Remove-Item -LiteralPath (Join-Path $mutDir 'extra-dir') -Force

$lockedPath = Join-Path $mutDir 'locked.bin'
Set-Content -LiteralPath $lockedPath -Value 'locked' -NoNewline -Encoding utf8
$lockedBase = Get-OutputManifest -Root $mutDir
$lockHandle = [System.IO.File]::Open($lockedPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::None)
try {
    $lockedManifest = Get-OutputManifest -Root $mutDir
}
finally {
    $lockHandle.Dispose()
}
Assert-True (-not [bool]$lockedManifest.Ok) 'B8-manifest-locked-file-fails'
Assert-Contains (@($lockedManifest.Reasons) -join '|') 'read-error:locked.bin' 'B8-manifest-locked-file-reason'
Remove-Item -LiteralPath $lockedPath -Force

$missingManifest = Get-OutputManifest -Root (Join-Path $testRoot 'no-such-output')
Assert-True (-not [bool]$missingManifest.Ok) 'B8-manifest-missing-root-fails'
Assert-Eq 'root-missing' (@($missingManifest.Reasons) -join '|') 'B8-manifest-missing-root-reason'

$zipEqualPath = Join-Path $testRoot 'equal.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($outBase, $zipEqualPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
$zipEqual = Get-ZipManifest -ZipPath $zipEqualPath
Assert-True ([bool]$zipEqual.Ok) 'B8-zip-manifest-ok'
Assert-Eq 3 $zipEqual.FileCount 'B8-zip-file-count'
$zipEqualCheck = Test-ZipMatchesOutput -ZipManifest $zipEqual -OutputManifest $outManifest -RootPrefix 'out-base'
Assert-True ([bool]$zipEqualCheck.Ok) 'B8-zip-equal-ok'
Assert-Eq $outManifest.Digest $zipEqualCheck.EntriesDigest 'B8-zip-equal-digest'
Assert-Eq 3 $zipEqualCheck.EntryCount 'B8-zip-equal-entry-count'
$zipTrailingPrefix = Test-ZipMatchesOutput -ZipManifest $zipEqual -OutputManifest $outManifest -RootPrefix 'out-base/'
Assert-True ([bool]$zipTrailingPrefix.Ok) 'B8-zip-prefix-trailing-slash-ok'
$zipWrongPrefix = Test-ZipMatchesOutput -ZipManifest $zipEqual -OutputManifest $outManifest -RootPrefix 'wrong-prefix'
Assert-True (-not [bool]$zipWrongPrefix.Ok) 'B8-zip-wrong-prefix-fails'
Assert-Contains (@($zipWrongPrefix.Reasons) -join '|') 'zip-entry-outside-root:out-base/IshWY.exe' 'B8-zip-wrong-prefix-reason'
$zipEmptyPrefix = Test-ZipMatchesOutput -ZipManifest $zipEqual -OutputManifest $outManifest -RootPrefix ''
Assert-True (-not [bool]$zipEmptyPrefix.Ok) 'B8-zip-empty-prefix-fails'
Assert-Contains (@($zipEmptyPrefix.Reasons) -join '|') 'root-prefix-empty' 'B8-zip-empty-prefix-reason'

$exeContent = Get-Content -LiteralPath (Join-Path $outBase 'IshWY.exe') -Raw -Encoding utf8
$priContent = Get-Content -LiteralPath (Join-Path $outBase 'IshWY.pri') -Raw -Encoding utf8
$dllContent = Get-Content -LiteralPath (Join-Path $outBase 'sub\a.dll') -Raw -Encoding utf8

$zipChangedPath = Join-Path $testRoot 'changed.zip'
New-TestZip -Path $zipChangedPath -Entries @(
    @{ name = 'out-base/IshWY.exe'; content = $exeContent },
    @{ name = 'out-base/IshWY.pri'; content = $priContent },
    @{ name = 'out-base/sub/a.dll'; content = 'dll-bytes-CHANGED' },
    @{ name = 'out-base/emptydir/'; content = $null }
)
$zipChanged = Get-ZipManifest -ZipPath $zipChangedPath
$zipChangedCheck = Test-ZipMatchesOutput -ZipManifest $zipChanged -OutputManifest $outManifest -RootPrefix 'out-base'
Assert-True (-not [bool]$zipChangedCheck.Ok) 'B8-zip-content-change-fails'
Assert-Eq 'sub/a.dll' (@($zipChangedCheck.Changed) -join '|') 'B8-zip-content-change-item'
Assert-Contains (@($zipChangedCheck.Reasons) -join '|') 'zip-file-changed:sub/a.dll' 'B8-zip-content-change-reason'

$zipExtraPath = Join-Path $testRoot 'extra.zip'
New-TestZip -Path $zipExtraPath -Entries @(
    @{ name = 'out-base/IshWY.exe'; content = $exeContent },
    @{ name = 'out-base/IshWY.pri'; content = $priContent },
    @{ name = 'out-base/sub/a.dll'; content = $dllContent },
    @{ name = 'out-base/emptydir/'; content = $null },
    @{ name = 'out-base/extra.dll'; content = 'extra' }
)
$zipExtra = Get-ZipManifest -ZipPath $zipExtraPath
$zipExtraCheck = Test-ZipMatchesOutput -ZipManifest $zipExtra -OutputManifest $outManifest -RootPrefix 'out-base'
Assert-True (-not [bool]$zipExtraCheck.Ok) 'B8-zip-extra-file-fails'
Assert-Eq 'extra.dll' (@($zipExtraCheck.Added) -join '|') 'B8-zip-extra-file-item'

$zipMissingPath = Join-Path $testRoot 'missing.zip'
New-TestZip -Path $zipMissingPath -Entries @(
    @{ name = 'out-base/IshWY.exe'; content = $exeContent },
    @{ name = 'out-base/IshWY.pri'; content = $priContent },
    @{ name = 'out-base/emptydir/'; content = $null }
)
$zipMissing = Get-ZipManifest -ZipPath $zipMissingPath
$zipMissingCheck = Test-ZipMatchesOutput -ZipManifest $zipMissing -OutputManifest $outManifest -RootPrefix 'out-base'
Assert-True (-not [bool]$zipMissingCheck.Ok) 'B8-zip-missing-file-fails'
Assert-Eq 'sub/a.dll' (@($zipMissingCheck.Removed) -join '|') 'B8-zip-missing-file-item'

$zipDuplicatePath = Join-Path $testRoot 'duplicate.zip'
New-TestZip -Path $zipDuplicatePath -Entries @(
    @{ name = 'out-base/IshWY.exe'; content = $exeContent },
    @{ name = 'out-base/IshWY.exe'; content = $exeContent },
    @{ name = 'out-base/IshWY.pri'; content = $priContent },
    @{ name = 'out-base/sub/a.dll'; content = $dllContent }
)
$zipDuplicate = Get-ZipManifest -ZipPath $zipDuplicatePath
Assert-True (-not [bool]$zipDuplicate.Ok) 'B8-zip-duplicate-entry-fails'
Assert-Contains (@($zipDuplicate.Reasons) -join '|') 'zip-entry-duplicate:' 'B8-zip-duplicate-reason'

$unsafeCases = @(
    @{ name = 'traversal'; entry = 'out-base/../evil.txt'; needle = 'zip-entry-unsafe-path:' },
    @{ name = 'absolute'; entry = '/abs.txt'; needle = 'zip-entry-unsafe-path:' },
    @{ name = 'backslash'; entry = 'out-base\back.txt'; needle = 'zip-entry-unsafe-path:' },
    @{ name = 'doubleslash'; entry = 'out-base//double.txt'; needle = 'zip-entry-unsafe-path:' }
)
foreach ($case in $unsafeCases) {
    $unsafePath = Join-Path $testRoot ('unsafe-' + $case.name + '.zip')
    New-TestZip -Path $unsafePath -Entries @(@{ name = $case.entry; content = 'evil' })
    $unsafeManifest = Get-ZipManifest -ZipPath $unsafePath
    Assert-True (-not [bool]$unsafeManifest.Ok) ('B8-zip-unsafe-' + $case.name + '-fails')
    Assert-Contains (@($unsafeManifest.Reasons) -join '|') $case.needle ('B8-zip-unsafe-' + $case.name + '-reason')
}

$outsidePath = Join-Path $testRoot 'outside.zip'
New-TestZip -Path $outsidePath -Entries @(@{ name = 'other/a.txt'; content = 'outside' })
$outsideManifest = Get-ZipManifest -ZipPath $outsidePath
$outsideCheck = Test-ZipMatchesOutput -ZipManifest $outsideManifest -OutputManifest $outManifest -RootPrefix 'out-base'
Assert-True (-not [bool]$outsideCheck.Ok) 'B8-zip-outside-prefix-fails'
Assert-Contains (@($outsideCheck.Reasons) -join '|') 'zip-entry-outside-root:other/a.txt' 'B8-zip-outside-prefix-reason'

$extraDirPath = Join-Path $testRoot 'extra-dir.zip'
New-TestZip -Path $extraDirPath -Entries @(
    @{ name = 'out-base/IshWY.exe'; content = $exeContent },
    @{ name = 'out-base/IshWY.pri'; content = $priContent },
    @{ name = 'out-base/sub/a.dll'; content = $dllContent },
    @{ name = 'out-base/emptydir/'; content = $null },
    @{ name = 'out-base/other/'; content = $null }
)
$extraDirManifest = Get-ZipManifest -ZipPath $extraDirPath
$extraDirCheck = Test-ZipMatchesOutput -ZipManifest $extraDirManifest -OutputManifest $outManifest -RootPrefix 'out-base'
Assert-True (-not [bool]$extraDirCheck.Ok) 'B8-zip-extra-directory-fails'
Assert-Eq 'other' (@($extraDirCheck.ExtraDirectories) -join '|') 'B8-zip-extra-directory-item'

$corruptPath = Join-Path $testRoot 'corrupt.zip'
Set-Content -LiteralPath $corruptPath -Value 'this is not a zip archive' -Encoding utf8
$corruptManifest = Get-ZipManifest -ZipPath $corruptPath
Assert-True (-not [bool]$corruptManifest.Ok) 'B8-zip-corrupt-fails'
Assert-Contains (@($corruptManifest.Reasons) -join '|') 'zip-open-failed' 'B8-zip-corrupt-reason'

$emptyOut = Join-Path $testRoot 'out-empty'
New-Item -ItemType Directory -Path $emptyOut -Force | Out-Null
$emptyOutManifest = Get-OutputManifest -Root $emptyOut
$emptyZipPath = Join-Path $testRoot 'empty.zip'
New-TestZip -Path $emptyZipPath -Entries @()
$emptyZipManifest = Get-ZipManifest -ZipPath $emptyZipPath
$emptyCheck = Test-ZipMatchesOutput -ZipManifest $emptyZipManifest -OutputManifest $emptyOutManifest -RootPrefix 'out-empty'
Assert-True (-not [bool]$emptyCheck.Ok) 'B8-zip-empty-fails'
Assert-Contains (@($emptyCheck.Reasons) -join '|') 'zip-entries-empty' 'B8-zip-empty-reason'

Assert-True (Test-SafeArchiveEntryPath 'out-base/a.txt') 'B8-safe-path-normal'
Assert-True (Test-SafeArchiveEntryPath 'out-base/') 'B8-safe-path-directory'
Assert-True (-not (Test-SafeArchiveEntryPath 'out-base/../x')) 'B8-safe-path-traversal'
Assert-True (-not (Test-SafeArchiveEntryPath '/abs.txt')) 'B8-safe-path-absolute'
Assert-True (-not (Test-SafeArchiveEntryPath 'out-base\a.txt')) 'B8-safe-path-backslash'
Assert-True (-not (Test-SafeArchiveEntryPath 'C:/a.txt')) 'B8-safe-path-drive'
Assert-True (-not (Test-SafeArchiveEntryPath 'a//b')) 'B8-safe-path-double-slash'
Assert-True (-not (Test-SafeArchiveEntryPath ('x' * 1100))) 'B8-safe-path-too-long'

$verdictDefault = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $true -SmokeStatus 'passed' -ZipCreated $true -Mode 'ci' -Dirty $true
Assert-Eq 'failed' $verdictDefault.Verdict 'B8-verdict-default-output-unstable-fails'
Assert-Contains (@($verdictDefault.HardFailures) -join '|') 'output-changed-during-gate' 'B8-verdict-output-unstable-reason'
$verdictStable = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $true -SmokeStatus 'passed' -ZipCreated $true -Mode 'ci' -Dirty $true -OutputStable $true
Assert-Eq 'passed' $verdictStable.Verdict 'B8-verdict-output-stable-passes'
$verdictNoZip = Get-GateVerdict -CorePassed $true -ArtifactVerified $true -SourceCovered $true -Stable $true -SmokeStatus 'passed' -ZipCreated $false -Mode 'ci' -Dirty $true -OutputStable $true
Assert-Eq 'limited' $verdictNoZip.Verdict 'B8-verdict-no-zip-is-limited'
Assert-Eq 2 $verdictNoZip.ExitCode 'B8-verdict-no-zip-exit-2'

foreach ($needle in @('artifact-manifest-before.json', 'artifact-manifest-after.json', 'Compare-OutputManifests', 'Get-ZipManifest', 'Test-ZipMatchesOutput', 'output-unstable', 'entriesVerified', 'zip-entries.json')) {
    Assert-Contains $gateText $needle ('B8-gate-wiring-' + $needle)
}
$manifestCompareIndex = $gateText.IndexOf('Compare-OutputManifests', [StringComparison]::Ordinal)
$zipStepIndex = $gateText.IndexOf('Get-ZipManifest', [StringComparison]::Ordinal)
Assert-True ($manifestCompareIndex -ge 0 -and $zipStepIndex -ge 0 -and $manifestCompareIndex -lt $zipStepIndex) 'B8-gate-compare-before-zip'
$entriesVerifiedIndex = $gateText.IndexOf('entriesVerified = $true', [StringComparison]::Ordinal)
$createdIndex = $gateText.IndexOf('createdThisRun = $true', [StringComparison]::Ordinal)
Assert-True ($entriesVerifiedIndex -ge 0 -and $createdIndex -ge 0 -and $entriesVerifiedIndex -lt $createdIndex) 'B8-gate-created-only-after-entries-verified'

Write-Host ''
Write-Host '== B9：ignored 未知目录/扩展默认覆盖 =='

$gitRepo = Join-Path $testRoot 'git-repo'
New-Item -ItemType Directory -Path (Join-Path $gitRepo 'src\generated') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $gitRepo 'newtop\src') -Force | Out-Null
Set-Content -LiteralPath (Join-Path $gitRepo '.gitignore') -Value @('newtop/', 'src/generated/', '*.png', '*.log', '*.unknown-extension')
Set-Content -LiteralPath (Join-Path $gitRepo 'src\a.cs') -Value 'class A { }' -Encoding utf8
Set-Content -LiteralPath (Join-Path $gitRepo 'src\NativeMethods.txt') -Value 'native' -Encoding utf8
Set-Content -LiteralPath (Join-Path $gitRepo 'src\App.manifest') -Value '<assembly/>' -Encoding utf8
Set-Content -LiteralPath (Join-Path $gitRepo 'src\generated\NativeMethods.txt') -Value 'generated' -Encoding utf8
Set-Content -LiteralPath (Join-Path $gitRepo 'src\untracked.unknown-extension') -Value 'unknown-ext' -Encoding utf8
Set-Content -LiteralPath (Join-Path $gitRepo 'newtop\src\a.cs') -Value 'class N { }' -Encoding utf8
Set-Content -LiteralPath (Join-Path $gitRepo 'newtop\payload.unknown-extension') -Value 'payload' -Encoding utf8
Set-Content -LiteralPath (Join-Path $gitRepo 'newtop\screenshot.png') -Value 'png' -Encoding utf8
Set-Content -LiteralPath (Join-Path $gitRepo 'build.log') -Value 'log' -Encoding utf8
& git -C $gitRepo init -q 2>$null | Out-Null
& git -C $gitRepo config user.email 'p11@example.invalid'
& git -C $gitRepo config user.name 'P11 Fixture'
& git -C $gitRepo add 'src/a.cs' 'src/NativeMethods.txt' 'src/App.manifest' '.gitignore' 2>$null | Out-Null
& git -C $gitRepo commit -q -m 'fixture' 2>$null | Out-Null

$fingerprint1 = Get-SourceFingerprint -Root $gitRepo
$candidates = @($fingerprint1.IgnoredInputCandidates)
Assert-Eq 4 $candidates.Count 'B9-candidate-count-4'
foreach ($expectedCandidate in @('newtop/payload.unknown-extension', 'newtop/src/a.cs', 'src/generated/NativeMethods.txt', 'src/untracked.unknown-extension')) {
    Assert-True ($candidates -contains $expectedCandidate) ('B9-candidate-includes-' + $expectedCandidate)
}
Assert-True (-not ($candidates -contains 'newtop/screenshot.png')) 'B9-png-not-candidate'
Assert-True (-not ($candidates -contains 'build.log')) 'B9-log-not-candidate'
$inputPaths = @($fingerprint1.Inputs | ForEach-Object { [string]$_.path })
Assert-True ($inputPaths -contains 'src/a.cs') 'B9-tracked-input-present'
Assert-True (-not ($inputPaths -contains '.gitignore')) 'B9-known-exclusion-not-input'
Assert-True (-not ($inputPaths -contains 'src/generated/NativeMethods.txt')) 'B9-ignored-not-silently-input'
$excludedPaths = @($fingerprint1.ExcludedInputs | ForEach-Object { [string]$_.path })
Assert-True ($excludedPaths -contains '.gitignore') 'B9-excluded-inputs-readable'

$fingerprintBase = $fingerprint1.Fingerprint
Set-Content -LiteralPath (Join-Path $gitRepo 'src\a.cs') -Value 'class A { int x; }' -Encoding utf8
$fingerprintModified = Get-SourceFingerprint -Root $gitRepo
Assert-True ($fingerprintModified.Fingerprint -ne $fingerprintBase) 'B9-tracked-modify-changes-fingerprint'
Set-Content -LiteralPath (Join-Path $gitRepo 'src\a.cs') -Value 'class A { }' -Encoding utf8
Set-Content -LiteralPath (Join-Path $gitRepo 'src\new.cs') -Value 'class B { }' -Encoding utf8
$fingerprintAdded = Get-SourceFingerprint -Root $gitRepo
Assert-True ($fingerprintAdded.Fingerprint -ne $fingerprintBase) 'B9-untracked-add-changes-fingerprint'
Remove-Item -LiteralPath (Join-Path $gitRepo 'src\new.cs') -Force
Remove-Item -LiteralPath (Join-Path $gitRepo 'src\NativeMethods.txt') -Force
$fingerprintDeleted = Get-SourceFingerprint -Root $gitRepo
Assert-True ($fingerprintDeleted.Fingerprint -ne $fingerprintBase) 'B9-delete-changes-fingerprint'
Assert-True ($fingerprintDeleted.IgnoredInputCandidates.Count -eq 4) 'B9-candidates-stable-after-mutations'

Write-Host ''
Write-Host '== M2/M3：大输出、2 秒超时与只清理自有进程 =='

$large = Invoke-Smoke -ScriptPath $smokeScript -ProcessArguments ($baseArguments + 'large-output') -KeepRoot
$largeText = $large.Stdout + [Environment]::NewLine + $large.Stderr
Assert-Eq 0 $large.ExitCode 'M2-large-output-exit-0'
Assert-Contains $largeText 'STUB-STDOUT-END' 'M2-large-stdout-complete'
Assert-Contains $largeText 'STUB-STDERR-END' 'M2-large-stderr-complete'
Assert-True ($large.Stdout.Length -gt 1000000) ('M2-large-combined-length ' + $large.Stdout.Length)
$largeRoot = Get-Marker $largeText 'SMOKE_ROOT'
if ($null -ne $largeRoot -and (Test-Path -LiteralPath $largeRoot)) { Remove-Item -LiteralPath $largeRoot -Recurse -Force }

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
$timedOut = Invoke-Smoke -ScriptPath $smokeScript -ProcessArguments ($baseArguments + 'sleep') -TimeoutSeconds 2
$stopwatch.Stop()
$timeoutText = $timedOut.Stdout + [Environment]::NewLine + $timedOut.Stderr
Assert-Eq 1 $timedOut.ExitCode 'M2-timeout-exit-1'
Assert-Contains $timeoutText 'timeout-2s' 'M2-timeout-reason'
Assert-True ($stopwatch.Elapsed.TotalSeconds -lt 25) ('M2-timeout-bounded-seconds ' + [math]::Round($stopwatch.Elapsed.TotalSeconds, 1))
$pidMatch = [regex]::Match($timedOut.Stdout, 'pid=(\d+)')
Assert-True $pidMatch.Success 'M2-timeout-child-pid-present'
if ($pidMatch.Success) {
    $childPid = [int]$pidMatch.Groups[1].Value
    Start-Sleep -Milliseconds 500
    $alive = $null -ne (Get-Process -Id $childPid -ErrorAction SilentlyContinue)
    Assert-True (-not $alive) 'M2-timeout-child-process-gone'
}
Assert-Contains $smokeText '$process.Kill()' 'M2-kills-own-process-object'
Assert-True (-not $smokeText.Contains('Stop-Process', [StringComparison]::Ordinal)) 'M2-no-stop-process-by-name'
Assert-True (-not $smokeText.Contains('taskkill', [StringComparison]::Ordinal)) 'M2-no-taskkill'
Assert-Contains $smokeText '临时冒烟根清理失败' 'M3-cleanup-warning-only'

Write-Host ''
if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Host ('FAILED: ' + $failure) -ForegroundColor Red }
    Write-Host ('测试目录保留：' + $testRoot)
    Write-Host ('==> P11 BEHAVIOR FAIL：{0}/{1}' -f $failures.Count, $checks) -ForegroundColor Red
    exit 1
}
Remove-Item -LiteralPath $testRoot -Recurse -Force
Write-Host ('==> P11 BEHAVIOR PASS：{0}/{0}' -f $checks) -ForegroundColor Green
exit 0
