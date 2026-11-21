#Requires -Version 7.0
<#
.SYNOPSIS
  IshWY --smoke v1 的可信验收器：在临时根下启动被测 exe，用 lib/SmokeResult.ps1
  校验结果文件的 nonce、本次子进程 PID、实际 exe 路径、隔离根、success、三项 checks 与 error。

.DESCRIPTION
- root 固定为 <TEMP>/IshWY-smoke/<nonce>，由本脚本新建并要求初始为空。
- 只等待/终止自己 Start 出来的进程对象，绝不按名称清理，不碰用户实例。
- 子进程 stdout/stderr 在等待退出前异步读取（M2）：输出超过管道缓冲也不会互相阻塞。
- argv 由本脚本唯一构造：无条件且只追加一次 --smoke（B7），调用者再传该参数直接拒绝；
  启动日志与 SMOKE_ARGV_JSON 都描述真实构造参数，不用显示字符串冒充。
- 30 秒有界等待；超时、缺结果、字段不符、checks 不齐、退出码非 0 都判失败。
- 清理临时根的失败只警告，不把已通过的判定翻成失败（M3）；失败时保留根目录取证。
- 成功仅代表 XAML 初始化 + 主窗 Loaded + UI 队列往返成立，不代表热键/截图/GUI 交互已验证。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [ValidateRange(1, 120)]
    [int]$TimeoutSeconds = 30,

    [switch]$KeepRoot,

    # 仅离线用例/诊断使用：传给被测进程的参数。gate/publish 从不传，避免扩大真实调用面。
    [string[]]$ProcessArguments = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# B7：--smoke 由本脚本唯一注入；调用者再传同一个参数视为接口误用，直接拒绝（不静默去重）。
$modeArgument = '--smoke'
foreach ($argument in $ProcessArguments) {
    if ([string]::Equals([string]$argument, $modeArgument, [StringComparison]::Ordinal)) {
        throw "ProcessArguments 不得包含 '$modeArgument'：smoke.ps1 会自行追加且只追加一次。"
    }
}

$libDir = Join-Path $PSScriptRoot 'lib'
. (Join-Path $libDir 'SmokeResult.ps1')

$exe = [System.IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ExePath).ProviderPath)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "找不到可执行文件：$exe"
}

$tempBase = [System.IO.Path]::GetTempPath()
$nonce = [Guid]::NewGuid().ToString('N')
$root = ConvertTo-NormalizedPath ([System.IO.Path]::Combine($tempBase, 'IshWY-smoke', $nonce))
$resultPath = Join-Path $root 'smoke-result.json'

# root 必须是本次新建的空目录；这是“不能有真实数据”的第一道保证。
New-Item -ItemType Directory -Path $root -Force | Out-Null
if (@(Get-ChildItem -LiteralPath $root -Force).Count -ne 0) {
    throw "冒烟根不是空目录：$root"
}

$startInfo = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $exe
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.WorkingDirectory = [System.IO.Path]::GetDirectoryName($exe)
$startInfo.Environment['ISHWY_SMOKE_ROOT'] = $root
$startInfo.Environment['ISHWY_SMOKE_NONCE'] = $nonce

# B7：argv 的唯一构造点。--smoke 固定在 ProcessArguments 之后追加，既不依赖调用者/env/日志，
# 也让 pwsh -File 替身能原样收到；构造完成后再次核对「恰一个」。
$effectiveArguments = [System.Collections.Generic.List[string]]::new()
foreach ($argument in $ProcessArguments) {
    [void]$effectiveArguments.Add([string]$argument)
}
[void]$effectiveArguments.Add($modeArgument)
$modeArgumentCount = @($effectiveArguments | Where-Object { [string]::Equals([string]$_, $modeArgument, [StringComparison]::Ordinal) }).Count
if ($modeArgumentCount -ne 1) {
    throw ("内部错误：构造出的 argv 不恰含一个 {0}（实际 {1} 个）" -f $modeArgument, $modeArgumentCount)
}
foreach ($argument in $effectiveArguments) {
    [void]$startInfo.ArgumentList.Add($argument)
}

$process = $null
$processId = 0
$exitCode = $null
$timedOut = $false
$stdout = ''
$stderr = ''
$stdoutRead = $true
$stderrRead = $true
$stdoutTask = $null
$stderrTask = $null

try {
    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'Process.Start 返回 null'
    }
    $processId = $process.Id
    # 日志描述真实构造参数（含调用方 ProcessArguments），不再用固定的显示字符串。
    $displayArguments = foreach ($argument in $effectiveArguments) {
        if ([string]$argument -match '[\s"]') {
            '"' + ([string]$argument).Replace('"', '\"') + '"'
        }
        else {
            [string]$argument
        }
    }
    Write-Host ("==> 启动：{0} {1} pid={2}" -f $exe, (@($displayArguments) -join ' '), $processId)
    Write-Host ('SMOKE_ARGV_JSON=' + (ConvertTo-Json -Compress -InputObject $effectiveArguments.ToArray()))
    Write-Host ("    root={0}" -f $root)

    # M2：先开始异步读取，再等待退出；否则子进程写满管道会互相阻塞到超时。
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $timedOut = $true
        # 只终止自己 Start 出来的进程对象，不按名字/路径清理，绝不碰用户实例。
        try { $process.Kill() } catch { }
        [void]$process.WaitForExit(5000)
    }
    else {
        $exitCode = $process.ExitCode
    }

    if ($stdoutTask.Wait(5000) -and $stdoutTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
        $stdout = $stdoutTask.Result
    }
    else {
        $stdoutRead = $false
    }
    if ($stderrTask.Wait(5000) -and $stderrTask.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
        $stderr = $stderrTask.Result
    }
    else {
        $stderrRead = $false
    }
}
finally {
    if ($null -ne $process) {
        $process.Dispose()
    }
}

$reasons = [System.Collections.Generic.List[string]]::new()
if ($timedOut) {
    $reasons.Add(("timeout-{0}s" -f $TimeoutSeconds))
}
if ($null -eq $exitCode) {
    if (-not $timedOut) {
        $reasons.Add('exit-code-unknown')
    }
}
elseif ($exitCode -ne 0) {
    $reasons.Add(("exit-code-{0}" -f $exitCode))
}
if (-not $stdoutRead) {
    $reasons.Add('stdout-read-incomplete')
}
if (-not $stderrRead) {
    $reasons.Add('stderr-read-incomplete')
}

if ($stdout.Trim().Length -gt 0) {
    Write-Host '--- stdout ---'
    Write-Host $stdout.Trim()
}
if ($stderr.Trim().Length -gt 0) {
    Write-Host '--- stderr ---'
    Write-Host $stderr.Trim()
}

if (Test-Path -LiteralPath $resultPath -PathType Leaf) {
    Write-Host '--- smoke-result.json ---'
    Get-Content -LiteralPath $resultPath -Raw -Encoding utf8 | Write-Host
}

# 稳定机器可读标记：调用方（gate.ps1）据此复制结果，不解析人类可读段落。
Write-Host ("SMOKE_ROOT=" + $root)
Write-Host ("RESULT_PATH=" + $resultPath)

$verdict = Test-SmokeResultFile -Root $root -Nonce $nonce -ProcessId $processId -ExePath $exe
foreach ($reason in $verdict.Reasons) {
    $reasons.Add($reason)
}

if ($reasons.Count -eq 0) {
    Write-Host '==> SMOKE PASS（三项 checks 齐全且匹配，退出码 0）' -ForegroundColor Green
    Write-Host '    仅证明 XAML 初始化 + 主窗 Loaded + UI 队列往返；不代表热键/截图/GUI 交互通过。'
    if (-not $KeepRoot) {
        # M3：清理失败只警告，不得把已通过的判定改成非 0；失败根留证。
        try {
            Remove-Item -LiteralPath $root -Recurse -Force
        }
        catch {
            Write-Warning ("临时冒烟根清理失败（本次判定仍为 PASS，根目录保留取证）：{0}" -f $_.Exception.Message)
        }
    }
    exit 0
}

Write-Host '==> SMOKE FAIL' -ForegroundColor Red
foreach ($reason in ($reasons | Select-Object -Unique)) {
    Write-Host ("    - " + $reason)
}
Write-Host ("    保留根目录便于取证：{0}" -f $root)
exit 1
