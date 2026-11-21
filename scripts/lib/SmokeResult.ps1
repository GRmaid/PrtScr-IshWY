#Requires -Version 7.0
<#
冒烟结果文件的独立校验器（--smoke v1）。只读文件，不启动进程、不碰真实数据目录。
失败原因码固定，便于离线负例与后续审查对齐。
#>

function ConvertTo-NormalizedPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ''
    }
    try {
        return [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
    }
    catch {
        return $Path
    }
}

function Test-IntegerValue {
    param($Value)
    # ConvertFrom-Json 在 PowerShell 7 会把 JSON 整数解析为 long；两者都算整数。
    return ($Value -is [int]) -or ($Value -is [long])
}

function Get-SmokeResultField {
    param($Object, [string]$Name)
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop) {
        return $null
    }
    return $prop.Value
}

function Test-SmokeResultFile {
    <#
    校验 <root>/smoke-result.json 是否满足冻结接口 v1：
    schemaVersion=1、nonce/pid/路径/根匹配、success=true、checks 恰为三项、error=null，
    且字段集合严格为 8 个。返回 { Ok, Reasons, Result, Path }。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Nonce,
        [Parameter(Mandatory = $true)][int]$ProcessId,
        [Parameter(Mandatory = $true)][string]$ExePath
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    $requiredFields = @('schemaVersion', 'nonce', 'processId', 'executablePath', 'dataRoot', 'success', 'checks', 'error')
    $requiredChecks = @('xaml-initialized', 'main-window-loaded', 'dispatcher-roundtrip')
    $normalizedRoot = ConvertTo-NormalizedPath $Root
    $normalizedExe = ConvertTo-NormalizedPath $ExePath
    $resultPath = Join-Path $normalizedRoot 'smoke-result.json'

    if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) {
        $reasons.Add('result-missing')
        return [pscustomobject]@{ Ok = $false; Reasons = @($reasons); Result = $null; Path = $resultPath }
    }

    $raw = Get-Content -LiteralPath $resultPath -Raw -Encoding utf8
    $result = $null
    try {
        $result = $raw | ConvertFrom-Json
    }
    catch {
        $reasons.Add('result-json-invalid')
        return [pscustomobject]@{ Ok = $false; Reasons = @($reasons); Result = $null; Path = $resultPath }
    }

    $names = @($result.PSObject.Properties.Name | Sort-Object)
    $expectedNames = @($requiredFields | Sort-Object)
    if (Compare-Object -ReferenceObject $expectedNames -DifferenceObject $names) {
        $reasons.Add('result-fields-mismatch')
    }

    $schemaVersion = Get-SmokeResultField $result 'schemaVersion'
    if (-not (Test-IntegerValue $schemaVersion) -or $schemaVersion -ne 1) {
        $reasons.Add('result-schema-version')
    }

    $nonceValue = Get-SmokeResultField $result 'nonce'
    if ($nonceValue -isnot [string] -or -not [string]::Equals($nonceValue, $Nonce, [StringComparison]::OrdinalIgnoreCase)) {
        $reasons.Add('result-nonce')
    }

    $pidValue = Get-SmokeResultField $result 'processId'
    if (-not (Test-IntegerValue $pidValue) -or $pidValue -ne $ProcessId) {
        $reasons.Add('result-process-id')
    }

    $exeValue = Get-SmokeResultField $result 'executablePath'
    if ($exeValue -isnot [string] -or
        -not [string]::Equals((ConvertTo-NormalizedPath $exeValue), $normalizedExe, [StringComparison]::OrdinalIgnoreCase)) {
        $reasons.Add('result-executable-path')
    }

    $dataRootValue = Get-SmokeResultField $result 'dataRoot'
    if ($dataRootValue -isnot [string] -or
        -not [string]::Equals((ConvertTo-NormalizedPath $dataRootValue), $normalizedRoot, [StringComparison]::OrdinalIgnoreCase)) {
        $reasons.Add('result-data-root')
    }

    $successValue = Get-SmokeResultField $result 'success'
    if ($successValue -isnot [bool] -or -not $successValue) {
        $reasons.Add('result-success')
    }

    $checksValue = Get-SmokeResultField $result 'checks'
    if ($null -eq $checksValue) {
        $reasons.Add('result-checks')
    }
    else {
        $actualChecks = @($checksValue)
        $allStrings = @($actualChecks | Where-Object { $_ -isnot [string] }).Count -eq 0
        $unique = @($actualChecks | Sort-Object -Unique)
        $sortedActual = @($actualChecks | Sort-Object)
        $sortedExpected = @($requiredChecks | Sort-Object)
        if (-not $allStrings -or
            $actualChecks.Count -ne $requiredChecks.Count -or
            $unique.Count -ne $requiredChecks.Count -or
            (Compare-Object -ReferenceObject $sortedExpected -DifferenceObject $sortedActual)) {
            $reasons.Add('result-checks')
        }
    }

    $errorValue = Get-SmokeResultField $result 'error'
    if ($null -ne $errorValue) {
        $reasons.Add('result-error')
    }

    return [pscustomobject]@{
        Ok = $reasons.Count -eq 0
        Reasons = @($reasons)
        Result = $result
        Path = $resultPath
    }
}
