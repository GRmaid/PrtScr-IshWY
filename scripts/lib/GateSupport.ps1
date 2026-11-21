#Requires -Version 7.0
<#
P-11 门禁支撑：唯一证据目录、命令回显、产物核验与门禁判决。
只做文件系统/字符串判断，不构建、不打包、不启动 App，便于离线用例直接调用。
#>

function New-RandomHexSuffix {
    param([int]$Bytes = 4)
    try {
        return [System.Security.Cryptography.RandomNumberGenerator]::GetHexString($Bytes).ToLowerInvariant()
    }
    catch {
        return ([guid]::NewGuid().ToString('N').Substring(0, $Bytes * 2))
    }
}

function New-EvidenceDirectory {
    <#
    唯一证据目录（M6）：yyyyMMdd-HHmmss-fff-<mode>-<8hex>。
    绝不复用已存在目录；-Timestamp/-Suffix 只给离线用例做确定性路径与冲突用例。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$EvidenceRoot,
        [Parameter(Mandatory = $true)][string]$Mode,
        [string]$Timestamp,
        [string]$Suffix,
        [int]$MaxAttempts = 5
    )

    if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
        throw '证据根为空'
    }
    if ($Timestamp -or $Suffix) {
        $stamp = if ($Timestamp) { $Timestamp } else { (Get-Date).ToString('yyyyMMdd-HHmmss-fff') }
        $tail = if ($Suffix) { $Suffix } else { New-RandomHexSuffix }
        $path = Join-Path $EvidenceRoot ("{0}-{1}-{2}" -f $stamp, $Mode, $tail)
        if (Test-Path -LiteralPath $path) {
            throw "证据目录已存在，拒绝复用：$path"
        }
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        return $path
    }

    for ($attempt = 0; $attempt -lt $MaxAttempts; $attempt++) {
        $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss-fff')
        $path = Join-Path $EvidenceRoot ("{0}-{1}-{2}" -f $stamp, $Mode, (New-RandomHexSuffix))
        if (Test-Path -LiteralPath $path) { continue }
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        if (@(Get-ChildItem -LiteralPath $path -Force).Count -eq 0) {
            return $path
        }
    }
    throw '无法创建唯一证据目录（连续冲突）'
}

function Format-CommandLine {
    <#
    审计用命令回显：含空白/引号的参数加引号，避免 manifest 里的命令因空格不可复现（M7）。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$Arguments = @()
    )
    $parts = @($FilePath) + @($Arguments)
    $quoted = foreach ($part in $parts) {
        $text = [string]$part
        if ($text -match '[\s"]') {
            '"' + ($text -replace '"', '\"') + '"'
        }
        else {
            $text
        }
    }
    return ($quoted -join ' ')
}

function Get-FileSha256 {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ArtifactVersionInfo {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{ Exists = $false; Readable = $false; ProductVersion = ''; FileVersion = ''; Error = 'artifact-missing' }
    }
    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        return [pscustomobject]@{
            Exists = $true
            Readable = $true
            ProductVersion = "$($info.ProductVersion)"
            FileVersion = "$($info.FileVersion)"
            Error = $null
        }
    }
    catch {
        return [pscustomobject]@{ Exists = $true; Readable = $false; ProductVersion = ''; FileVersion = ''; Error = $_.Exception.Message }
    }
}

function Test-PublishArtifact {
    <#
    发布 stage 之后、smoke 之前的硬断言（B2/B5）：
    - 输出目录存在且非空，IshWY.exe / IshWY.dll / IshWY.pri / *.xbf 齐全；
    - 托管 IshWY.dll 的 ProductVersion/FileVersion 与 manifest 版本精确一致（dll 为权威）；
    - IshWY.exe（apphost）若携带非零版本，必须同样一致；未携带则记录 dll-authoritative。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$OutDir,
        [Parameter(Mandatory = $true)][string]$Version,
        [string]$ExeName = 'IshWY.exe',
        [string]$DllName = 'IshWY.dll'
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    $result = [ordered]@{
        outDirExists = $false
        fileCount = 0
        totalBytes = 0
        exeExists = $false
        dllExists = $false
        priExists = $false
        xbfCount = 0
        dllProductVersion = ''
        dllFileVersion = ''
        exeProductVersion = ''
        exeFileVersion = ''
        exeVersionSource = 'unknown'
    }

    if (-not (Test-Path -LiteralPath $OutDir -PathType Container)) {
        $reasons.Add('outdir-missing')
        return [pscustomobject]@{ Ok = $false; Reasons = @($reasons); Detail = $result }
    }
    $result.outDirExists = $true

    $files = @(Get-ChildItem -LiteralPath $OutDir -Recurse -File)
    $result.fileCount = $files.Count
    $result.totalBytes = if ($files.Count -gt 0) { ($files | Measure-Object Length -Sum).Sum } else { 0 }
    if ($files.Count -eq 0) {
        $reasons.Add('artifact-empty')
    }

    $exePath = Join-Path $OutDir $ExeName
    $dllPath = Join-Path $OutDir $DllName
    $priPath = Join-Path $OutDir 'IshWY.pri'
    $result.exeExists = Test-Path -LiteralPath $exePath -PathType Leaf
    $result.dllExists = Test-Path -LiteralPath $dllPath -PathType Leaf
    $result.priExists = Test-Path -LiteralPath $priPath -PathType Leaf
    $result.xbfCount = @(Get-ChildItem -LiteralPath $OutDir -Recurse -Filter *.xbf -File).Count

    if (-not $result.exeExists) { $reasons.Add('exe-missing') }
    if (-not $result.dllExists) { $reasons.Add('dll-missing') }
    if (-not $result.priExists) { $reasons.Add('pri-missing') }
    if ($result.xbfCount -eq 0) { $reasons.Add('xbf-missing') }

    if ($result.dllExists) {
        $dllInfo = Get-ArtifactVersionInfo $dllPath
        $result.dllProductVersion = $dllInfo.ProductVersion
        $result.dllFileVersion = $dllInfo.FileVersion
        if (-not $dllInfo.Readable) {
            $reasons.Add("dll-version-unreadable: $($dllInfo.Error)")
        }
        else {
            $match = Test-ArtifactVersionMatch -Expected $Version -ProductVersion $dllInfo.ProductVersion -FileVersion $dllInfo.FileVersion
            foreach ($reason in $match.Reasons) {
                $reasons.Add("dll-$reason")
            }
        }
    }

    if ($result.exeExists) {
        $exeInfo = Get-ArtifactVersionInfo $exePath
        $result.exeProductVersion = $exeInfo.ProductVersion
        $result.exeFileVersion = $exeInfo.FileVersion
        $exeProduct = Get-NormalizedProductVersion $exeInfo.ProductVersion
        # apphost 可能不携版本（空/0.0.0[.0]）：此时以托管 dll 为权威并显式记录。
        if ([string]::IsNullOrWhiteSpace($exeProduct) -or $exeProduct -match '^0\.0\.0(\.0)?$') {
            $result.exeVersionSource = 'dll-authoritative'
        }
        elseif (-not $exeInfo.Readable) {
            $result.exeVersionSource = 'unreadable'
            $reasons.Add("exe-version-unreadable: $($exeInfo.Error)")
        }
        else {
            $result.exeVersionSource = 'apphost'
            $match = Test-ArtifactVersionMatch -Expected $Version -ProductVersion $exeInfo.ProductVersion -FileVersion $exeInfo.FileVersion
            foreach ($reason in $match.Reasons) {
                $reasons.Add("exe-$reason")
            }
        }
    }

    return [pscustomobject]@{ Ok = $reasons.Count -eq 0; Reasons = @($reasons); Detail = $result }
}

# ==== B8：输出清单与 ZIP 条目绑定 ====
# 证据边界：只比对本次门禁采样到的普通文件与 ZIP 条目；不提供 OS 沙箱，
# 不声称能防住同权限恶意进程在采样间隙的瞬时替换/并发写入。

function Get-OutputManifest {
    <#
    smoke 前后的确定性输出清单（B8）：相对路径（/ 分隔）、长度、SHA256。
    递归枚举普通文件与目录；读取/哈希失败记为原因并 Ok=false，调用方必须失败关闭。
    #>
    param([Parameter(Mandatory = $true)][string]$Root)

    $reasons = [System.Collections.Generic.List[string]]::new()
    $entries = [System.Collections.Generic.List[object]]::new()
    $directories = [System.Collections.Generic.List[string]]::new()

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        return [pscustomobject]@{
            Ok = $false; Reasons = @('root-missing'); Root = $Root
            Entries = @(); Directories = @(); FileCount = 0; DirectoryCount = 0
            TotalBytes = [int64]0; Digest = $null
        }
    }
    $rootFull = (Resolve-Path -LiteralPath $Root).ProviderPath
    $rootFull = [System.IO.Path]::TrimEndingDirectorySeparator($rootFull)

    $fileItems = @(Get-ChildItem -LiteralPath $rootFull -Recurse -File -Force -ErrorAction Stop)
    foreach ($file in $fileItems) {
        $relative = [System.IO.Path]::GetRelativePath($rootFull, $file.FullName).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative -match '(^|/)\.\.(/|$)') {
            $reasons.Add('path-outside-root:' + $relative)
            continue
        }
        try {
            $length = [int64](Get-Item -LiteralPath $file.FullName -Force -ErrorAction Stop).Length
            $sha = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()
        }
        catch {
            $reasons.Add('read-error:' + $relative)
            continue
        }
        $entries.Add([pscustomobject]@{ path = $relative; sha256 = $sha; bytes = $length })
    }

    foreach ($directory in @(Get-ChildItem -LiteralPath $rootFull -Recurse -Directory -Force -ErrorAction Stop)) {
        $relative = [System.IO.Path]::GetRelativePath($rootFull, $directory.FullName).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or $relative -match '(^|/)\.\.(/|$)') {
            $reasons.Add('directory-outside-root:' + $relative)
            continue
        }
        $directories.Add($relative)
    }

    $entries.Sort([System.Comparison[object]] { param($left, $right) [string]::CompareOrdinal([string]$left.path, [string]$right.path) })
    $directories.Sort([StringComparer]::Ordinal)

    $builder = [System.Text.StringBuilder]::new()
    $totalBytes = [int64]0
    foreach ($entry in $entries) {
        [void]$builder.Append([string]$entry.path)
        [void]$builder.Append([char]0)
        [void]$builder.Append([string]$entry.sha256)
        [void]$builder.Append([char]0)
        [void]$builder.Append(([int64]$entry.bytes).ToString([System.Globalization.CultureInfo]::InvariantCulture))
        [void]$builder.Append([char]10)
        $totalBytes += [int64]$entry.bytes
    }
    $digest = $null
    if ($entries.Count -gt 0) {
        $digest = [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($builder.ToString()))
        ).ToLowerInvariant()
    }

    return [pscustomobject]@{
        Ok = $reasons.Count -eq 0
        Reasons = @($reasons)
        Root = $rootFull
        Entries = @($entries)
        Directories = @($directories)
        FileCount = $entries.Count
        DirectoryCount = $directories.Count
        TotalBytes = $totalBytes
        Digest = $digest
    }
}

function Compare-OutputManifests {
    <#
    比对 smoke 前后两份输出清单（B8）：任一增/删/改/目录变化或任一侧读取失败都 Ok=false。
    只描述本次采样事实，不推断变化原因。
    #>
    param($Before, $After)

    $reasons = [System.Collections.Generic.List[string]]::new()
    $added = [System.Collections.Generic.List[string]]::new()
    $removed = [System.Collections.Generic.List[string]]::new()
    $changed = [System.Collections.Generic.List[string]]::new()
    $addedDirectories = [System.Collections.Generic.List[string]]::new()
    $removedDirectories = [System.Collections.Generic.List[string]]::new()

    if ($null -eq $Before) {
        $reasons.Add('before-unavailable')
    }
    elseif (-not $Before.Ok) {
        foreach ($reason in @($Before.Reasons)) { $reasons.Add('before:' + $reason) }
    }
    if ($null -eq $After) {
        $reasons.Add('after-unavailable')
    }
    elseif (-not $After.Ok) {
        foreach ($reason in @($After.Reasons)) { $reasons.Add('after:' + $reason) }
    }

    $beforeMap = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $afterMap = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $beforeDirs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $afterDirs = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)

    if ($null -ne $Before -and $Before.Ok) {
        foreach ($entry in @($Before.Entries)) { $beforeMap[[string]$entry.path] = $entry }
        foreach ($directory in @($Before.Directories)) { [void]$beforeDirs.Add([string]$directory) }
    }
    if ($null -ne $After -and $After.Ok) {
        foreach ($entry in @($After.Entries)) { $afterMap[[string]$entry.path] = $entry }
        foreach ($directory in @($After.Directories)) { [void]$afterDirs.Add([string]$directory) }
    }

    if ($beforeMap.Count -gt 0 -or $afterMap.Count -gt 0) {
        foreach ($key in $afterMap.Keys) {
            if (-not $beforeMap.ContainsKey($key)) {
                $added.Add($key)
                continue
            }
            $beforeEntry = $beforeMap[$key]
            $afterEntry = $afterMap[$key]
            if (-not [string]::Equals([string]$beforeEntry.sha256, [string]$afterEntry.sha256, [StringComparison]::Ordinal) -or
                [int64]$beforeEntry.bytes -ne [int64]$afterEntry.bytes) {
                $changed.Add($key)
            }
        }
        foreach ($key in $beforeMap.Keys) {
            if (-not $afterMap.ContainsKey($key)) { $removed.Add($key) }
        }
    }
    if ($null -ne $Before -and $Before.Ok -and $null -ne $After -and $After.Ok) {
        foreach ($directory in $afterDirs) {
            if (-not $beforeDirs.Contains($directory)) { $addedDirectories.Add($directory) }
        }
        foreach ($directory in $beforeDirs) {
            if (-not $afterDirs.Contains($directory)) { $removedDirectories.Add($directory) }
        }
    }

    foreach ($list in @($added, $removed, $changed, $addedDirectories, $removedDirectories)) {
        $list.Sort([StringComparer]::Ordinal)
    }
    foreach ($item in $added) { $reasons.Add('file-added:' + $item) }
    foreach ($item in $removed) { $reasons.Add('file-removed:' + $item) }
    foreach ($item in $changed) { $reasons.Add('file-changed:' + $item) }
    foreach ($item in $addedDirectories) { $reasons.Add('directory-added:' + $item) }
    foreach ($item in $removedDirectories) { $reasons.Add('directory-removed:' + $item) }

    $ok = ($reasons.Count -eq 0 -and $added.Count -eq 0 -and $removed.Count -eq 0 -and
        $changed.Count -eq 0 -and $addedDirectories.Count -eq 0 -and $removedDirectories.Count -eq 0)

    return [pscustomobject]@{
        Ok = $ok
        Reasons = @($reasons)
        Added = @($added)
        Removed = @($removed)
        Changed = @($changed)
        AddedDirectories = @($addedDirectories)
        RemovedDirectories = @($removedDirectories)
        BeforeFileCount = if ($null -ne $Before) { $Before.FileCount } else { $null }
        AfterFileCount = if ($null -ne $After) { $After.FileCount } else { $null }
        BeforeDigest = if ($null -ne $Before) { $Before.Digest } else { $null }
        AfterDigest = if ($null -ne $After) { $After.Digest } else { $null }
    }
}

function Test-SafeArchiveEntryPath {
    <#
    ZIP 条目安全路径（B8）：拒绝空名、超长、反斜杠、NUL、双斜杠、绝对路径、盘符、
    空/./.. 段与冒号；允许目录条目以单个 '/' 结尾。
    #>
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) { return $false }
    if ($Name.Length -gt 1024) { return $false }
    if ($Name.Contains('\')) { return $false }
    if ($Name.Contains([char]0)) { return $false }
    if ($Name.Contains('//')) { return $false }
    if ($Name.StartsWith('/')) { return $false }
    if ($Name -match '^[A-Za-z]:') { return $false }
    $trimmed = $Name.TrimEnd('/')
    if ($trimmed.Length -eq 0) { return $false }
    foreach ($segment in ($trimmed -split '/')) {
        if ($segment -eq '' -or $segment -eq '.' -or $segment -eq '..') { return $false }
        if ($segment.Contains(':')) { return $false }
    }
    return $true
}

function Get-ZipManifest {
    <#
    独立枚举 ZIP 实际条目（B8）：不信任发布脚本的 ZIP_PATH/ZIP_SHA256 标记。
    逐文件读取内容并计算 SHA256；重复/非法路径/读取失败都 Ok=false。
    #>
    param([Parameter(Mandatory = $true)][string]$ZipPath)

    $reasons = [System.Collections.Generic.List[string]]::new()
    $files = [System.Collections.Generic.List[object]]::new()
    $directories = [System.Collections.Generic.List[string]]::new()
    $result = [pscustomobject]@{
        Ok = $false; Reasons = $reasons; Path = $ZipPath
        Files = @(); Directories = @(); FileCount = 0; DirectoryCount = 0
        TotalBytes = [int64]0; Digest = $null
    }
    if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) {
        $reasons.Add('zip-missing')
        return $result
    }

    $archive = $null
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    }
    catch {
        $reasons.Add('zip-open-failed')
        return $result
    }

    try {
        $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName
            if (-not (Test-SafeArchiveEntryPath $name)) {
                $reasons.Add('zip-entry-unsafe-path:' + $name)
                continue
            }
            $identity = $name.TrimEnd('/')
            if (-not $seen.Add($identity)) {
                $reasons.Add('zip-entry-duplicate:' + $name)
                continue
            }
            if ($name.EndsWith('/')) {
                $directories.Add($identity)
                continue
            }
            try {
                $stream = $entry.Open()
                try {
                    $hasher = [System.Security.Cryptography.SHA256]::Create()
                    try { $hashBytes = $hasher.ComputeHash($stream) }
                    finally { $hasher.Dispose() }
                }
                finally {
                    $stream.Dispose()
                }
                $sha = [Convert]::ToHexString($hashBytes).ToLowerInvariant()
                $length = [int64]$entry.Length
            }
            catch {
                $reasons.Add('zip-entry-read-error:' + $name)
                continue
            }
            $files.Add([pscustomobject]@{ path = $name; sha256 = $sha; bytes = $length })
        }
    }
    finally {
        $archive.Dispose()
    }

    $files.Sort([System.Comparison[object]] { param($left, $right) [string]::CompareOrdinal([string]$left.path, [string]$right.path) })
    $directories.Sort([StringComparer]::Ordinal)

    $builder = [System.Text.StringBuilder]::new()
    $totalBytes = [int64]0
    foreach ($entry in $files) {
        [void]$builder.Append([string]$entry.path)
        [void]$builder.Append([char]0)
        [void]$builder.Append([string]$entry.sha256)
        [void]$builder.Append([char]0)
        [void]$builder.Append(([int64]$entry.bytes).ToString([System.Globalization.CultureInfo]::InvariantCulture))
        [void]$builder.Append([char]10)
        $totalBytes += [int64]$entry.bytes
    }
    $digest = $null
    if ($files.Count -gt 0) {
        $digest = [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($builder.ToString()))
        ).ToLowerInvariant()
    }

    $result.Ok = $reasons.Count -eq 0
    $result.Files = @($files)
    $result.Directories = @($directories)
    $result.FileCount = $files.Count
    $result.DirectoryCount = $directories.Count
    $result.TotalBytes = $totalBytes
    $result.Digest = $digest
    return $result
}

function Test-ZipMatchesOutput {
    <#
    把 ZIP 实际条目绑定到 smoke 后输出清单（B8）：
    - 所有文件条目必须位于 <RootPrefix>/ 之下，相对路径唯一、无遍历/绝对路径；
    - 文件集合与逐文件 SHA256/长度必须与清单完全一致（多/少/改都失败）；
    - 目录条目只允许出现在清单已有目录上（隐式父目录不要求显式条目）；
    - 任何不安全/重复/歧义条目、ZIP 打开失败都 Ok=false。
    #>
    param(
        [Parameter(Mandatory = $true)]$ZipManifest,
        $OutputManifest,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$RootPrefix
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    $added = [System.Collections.Generic.List[string]]::new()
    $removed = [System.Collections.Generic.List[string]]::new()
    $changed = [System.Collections.Generic.List[string]]::new()
    $extraDirectories = [System.Collections.Generic.List[string]]::new()
    $result = [pscustomobject]@{
        Ok = $false; Reasons = $reasons; RootPrefix = $RootPrefix
        EntryCount = 0; TotalBytes = [int64]0; EntriesDigest = $null
        Added = @(); Removed = @(); Changed = @(); ExtraDirectories = @()
    }

    if ($null -eq $ZipManifest) {
        $reasons.Add('zip-manifest-missing')
    }
    elseif (-not $ZipManifest.Ok) {
        $reasons.Add('zip-manifest-not-ok')
        foreach ($reason in @($ZipManifest.Reasons)) { $reasons.Add('zip:' + $reason) }
    }
    if ($null -eq $OutputManifest) {
        $reasons.Add('output-manifest-missing')
    }
    elseif (-not $OutputManifest.Ok) {
        $reasons.Add('output-manifest-not-ok')
        foreach ($reason in @($OutputManifest.Reasons)) { $reasons.Add('output:' + $reason) }
    }
    if ($reasons.Count -gt 0) {
        return $result
    }

    $prefix = ([string]$RootPrefix).Replace('\', '/').Trim('/')
    if ([string]::IsNullOrWhiteSpace($prefix)) {
        $reasons.Add('root-prefix-empty')
        return $result
    }
    $prefixWithSlash = $prefix + '/'

    $expected = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in @($OutputManifest.Entries)) { $expected[[string]$entry.path] = $entry }
    $expectedDirectories = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($directory in @($OutputManifest.Directories)) { [void]$expectedDirectories.Add([string]$directory) }

    $actual = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in @($ZipManifest.Files)) {
        $name = [string]$entry.path
        if (-not $name.StartsWith($prefixWithSlash, [StringComparison]::Ordinal)) {
            $reasons.Add('zip-entry-outside-root:' + $name)
            continue
        }
        $relative = $name.Substring($prefixWithSlash.Length)
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains(':')) {
            $reasons.Add('zip-entry-ambiguous-path:' + $name)
            continue
        }
        if ($actual.ContainsKey($relative)) {
            $reasons.Add('zip-entry-duplicate-relative:' + $name)
            continue
        }
        $actual[$relative] = $entry
    }
    foreach ($directory in @($ZipManifest.Directories)) {
        $name = [string]$directory
        if (-not $name.StartsWith($prefixWithSlash, [StringComparison]::Ordinal)) {
            $reasons.Add('zip-directory-outside-root:' + $name)
            continue
        }
        $relative = $name.Substring($prefixWithSlash.Length)
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative.Contains(':')) {
            $reasons.Add('zip-directory-ambiguous-path:' + $name)
            continue
        }
        if (-not $expectedDirectories.Contains($relative)) {
            $extraDirectories.Add($relative)
        }
    }

    foreach ($relative in $actual.Keys) {
        if (-not $expected.ContainsKey($relative)) {
            $added.Add($relative)
            continue
        }
        $expectedEntry = $expected[$relative]
        $actualEntry = $actual[$relative]
        if (-not [string]::Equals([string]$expectedEntry.sha256, [string]$actualEntry.sha256, [StringComparison]::Ordinal) -or
            [int64]$expectedEntry.bytes -ne [int64]$actualEntry.bytes) {
            $changed.Add($relative)
        }
    }
    foreach ($relative in $expected.Keys) {
        if (-not $actual.ContainsKey($relative)) { $removed.Add($relative) }
    }

    foreach ($list in @($added, $removed, $changed, $extraDirectories)) {
        $list.Sort([StringComparer]::Ordinal)
    }
    foreach ($item in $added) { $reasons.Add('zip-file-added:' + $item) }
    foreach ($item in $removed) { $reasons.Add('zip-file-missing:' + $item) }
    foreach ($item in $changed) { $reasons.Add('zip-file-changed:' + $item) }
    foreach ($item in $extraDirectories) { $reasons.Add('zip-entry-extra-directory:' + $item) }

    $entriesBuilder = [System.Text.StringBuilder]::new()
    $totalBytes = [int64]0
    $relativeEntries = [System.Collections.Generic.List[object]]::new()
    foreach ($relative in $actual.Keys) {
        $entry = $actual[$relative]
        $relativeEntries.Add([pscustomobject]@{ path = $relative; sha256 = [string]$entry.sha256; bytes = [int64]$entry.bytes })
        $totalBytes += [int64]$entry.bytes
    }
    $relativeEntries.Sort([System.Comparison[object]] { param($left, $right) [string]::CompareOrdinal([string]$left.path, [string]$right.path) })
    foreach ($entry in $relativeEntries) {
        [void]$entriesBuilder.Append([string]$entry.path)
        [void]$entriesBuilder.Append([char]0)
        [void]$entriesBuilder.Append([string]$entry.sha256)
        [void]$entriesBuilder.Append([char]0)
        [void]$entriesBuilder.Append(([int64]$entry.bytes).ToString([System.Globalization.CultureInfo]::InvariantCulture))
        [void]$entriesBuilder.Append([char]10)
    }
    $entriesDigest = $null
    if ($relativeEntries.Count -gt 0) {
        $entriesDigest = [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($entriesBuilder.ToString()))
        ).ToLowerInvariant()
    }

    $ok = ($reasons.Count -eq 0 -and $added.Count -eq 0 -and $removed.Count -eq 0 -and
        $changed.Count -eq 0 -and $extraDirectories.Count -eq 0 -and $actual.Count -gt 0)
    if ($ok -and $null -ne $OutputManifest.Digest -and
        -not [string]::Equals([string]$entriesDigest, [string]$OutputManifest.Digest, [StringComparison]::Ordinal)) {
        $reasons.Add('zip-entries-digest-mismatch')
        $ok = $false
    }
    if ($actual.Count -eq 0) {
        $reasons.Add('zip-entries-empty')
    }

    $result.Ok = $ok
    $result.EntryCount = $actual.Count
    $result.TotalBytes = $totalBytes
    $result.EntriesDigest = $entriesDigest
    $result.Added = @($added)
    $result.Removed = @($removed)
    $result.Changed = @($changed)
    $result.ExtraDirectories = @($extraDirectories)
    return $result
}

function Get-GateVerdict {
    <#
    门禁判决（B4/B5/B6），离线可测：
    - 只有 core 全过 + 产物核验通过 + 源码输入无缺口 + 源码稳定 + smoke passed
      + 输出在 smoke 前后稳定（B8）+ 本次 ZIP 已生成且条目逐文件绑定，才是 passed(0)；
    - 硬失败（core/产物/覆盖/稳定/输出变化/smoke 失败/ZIP 失败）为 failed(1)；
    - -SkipSmoke / -NoZip / smoke 未跑 / 无本次 ZIP 为 limited(2)：输出 LIMITED，绝不 GATE PASS。
    releaseReady 只有 release 模式且 passed 且 clean 才为 true。
    #>
    param(
        [bool]$CorePassed = $false,
        [bool]$ArtifactVerified = $false,
        [bool]$SourceCovered = $false,
        [bool]$Stable = $false,
        [string]$SmokeStatus = 'not-run',
        [bool]$ZipCreated = $false,
        [string]$Mode = 'ci',
        [bool]$Dirty = $true,
        [bool]$SkipSmoke = $false,
        [bool]$NoZip = $false,
        [bool]$ZipFailed = $false,
        [bool]$OutputStable = $false
    )

    $hardFailures = [System.Collections.Generic.List[string]]::new()
    $reasons = [System.Collections.Generic.List[string]]::new()

    if (-not $CorePassed) { $hardFailures.Add('core-gates-not-passed') }
    if (-not $ArtifactVerified) { $hardFailures.Add('artifact-not-verified') }
    if (-not $SourceCovered) { $hardFailures.Add('source-input-coverage-gap') }
    if (-not $Stable) { $hardFailures.Add('source-changed-during-gate') }
    if (-not $OutputStable) { $hardFailures.Add('output-changed-during-gate') }
    if ($SmokeStatus -eq 'failed') { $hardFailures.Add('smoke-failed') }
    if ($ZipFailed) { $hardFailures.Add('zip-failed') }

    if ($SkipSmoke) { $reasons.Add('smoke-skipped-by-request') }
    elseif ($SmokeStatus -ne 'passed' -and $SmokeStatus -ne 'failed') { $reasons.Add('smoke-not-run') }
    if ($NoZip) { $reasons.Add('zip-skipped-by-request') }
    if (-not $NoZip -and -not $ZipCreated -and -not $ZipFailed) { $reasons.Add('zip-not-created-this-run') }

    if ($hardFailures.Count -gt 0) {
        $verdict = 'failed'
        $exitCode = 1
    }
    elseif ($reasons.Count -gt 0) {
        $verdict = 'limited'
        $exitCode = 2
    }
    else {
        $verdict = 'passed'
        $exitCode = 0
    }

    $complete = ($verdict -eq 'passed')
    $releaseReady = $complete -and ($Mode -eq 'release') -and (-not $Dirty)

    return [pscustomobject]@{
        Verdict = $verdict
        Complete = $complete
        ExitCode = $exitCode
        ReleaseReady = $releaseReady
        HardFailures = @($hardFailures)
        Reasons = @($reasons)
    }
}