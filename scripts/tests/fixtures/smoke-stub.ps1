#Requires -Version 7.0
<#
P11 离线冒烟替身（B7/M2/M3 夹具专用）。
不是真实 App：只读写 ISHWY_SMOKE_ROOT 下的临时文件，按 argv/env 场景输出，
用于证明 smoke.ps1 的真实 argv/env 行为、管道大输出与超时语义。
#>
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$argv = @($args | ForEach-Object { [string]$_ })
$root = [string]$env:ISHWY_SMOKE_ROOT
$nonce = [string]$env:ISHWY_SMOKE_NONCE
if ([string]::IsNullOrWhiteSpace($root)) {
    [Console]::Error.WriteLine('STUB: ISHWY_SMOKE_ROOT 缺失')
    exit 3
}
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
}

$modeCount = @($argv | Where-Object { [string]::Equals($_, '--smoke', [StringComparison]::Ordinal) }).Count
$scenario = 'default'
foreach ($argument in $argv) {
    if (-not [string]::Equals($argument, '--smoke', [StringComparison]::Ordinal)) {
        $scenario = $argument
        break
    }
}

# 无论是否有 --smoke 都落 argv/env 事实，供“env-only 不产生结果”负例取证。
$capture = [ordered]@{
    argv = $argv
    envRoot = $root
    envNonce = $nonce
    pid = $PID
    processPath = (Get-Process -Id $PID).Path
    modeCount = $modeCount
    scenario = $scenario
}
$capture | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'argv-capture.json') -Encoding utf8

if ($scenario -eq 'sleep') {
    # 用于 2 秒有界等待夹具；正常超时路径会被 smoke.ps1 只杀本进程对象。
    Start-Sleep -Seconds 20
    exit 0
}

if ($scenario -eq 'large-output') {
    # 每个流约 540KB，超过管道缓冲；若 smoke.ps1 不是先异步读再等待，这里会互相阻塞到超时。
    $stdoutBlock = 'STUB-STDOUT-' + ('x' * 512)
    for ($i = 0; $i -lt 1024; $i++) { Write-Output $stdoutBlock }
    Write-Output 'STUB-STDOUT-END'
    $stderrBlock = 'STUB-STDERR-' + ('y' * 512)
    for ($i = 0; $i -lt 1024; $i++) { [Console]::Error.WriteLine($stderrBlock) }
    [Console]::Error.WriteLine('STUB-STDERR-END')
}

if ($scenario -eq 'no-result') {
    # 退出码 0 但不写结果文件：必须被判 result-missing，而不是 0 就通过。
    exit 0
}

if ($modeCount -ne 1) {
    # env-only 负例：没有 --smoke 就没有结果文件，模拟“只靠环境变量”的假成功。
    exit 0
}

$result = [ordered]@{
    schemaVersion = 1
    nonce = $nonce
    processId = $PID
    executablePath = (Get-Process -Id $PID).Path
    dataRoot = $root
    success = $true
    checks = @('xaml-initialized', 'main-window-loaded', 'dispatcher-roundtrip')
    error = $null
}
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $root 'smoke-result.json') -Encoding utf8
exit 0
