#Requires -Version 7.0
<#
P-11 源码身份、版本规则与构建输入覆盖。只读 git 工作树；不构建、不打包、不启动 App。

构建输入采用「默认纳入、显式排除」：
- 只排除生成物目录（bin/obj/.tmp/artifacts/data/publish/node_modules 等）与非构建资料
  （Record/docs/README/LICENSE/.gitignore/.gitattributes）；
- 其余 tracked + untracked（未忽略）文件全部纳入指纹，因此 .txt/.xml/config 及未来新类型
  不会静默漏检（B3）；
- 被 gitignore 的文件默认也是覆盖缺口：只有生成物/非构建资料规则与显式非构建后缀
  （截图/日志，见 $IshWYNonBuildIgnoredExtensions）不报缺口；未知目录、未知扩展名、
  无扩展名文件一律列为 IgnoredInputCandidates，由完整门禁失败关闭（B9）；
- 被排除的 tracked 文件写入 ExcludedInputs 供证据审计。
#>

if (-not (Get-Command -Name ConvertTo-NormalizedPath -ErrorAction SilentlyContinue)) {
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
}

$script:IshWYExcludedSegments = @(
    'bin', 'obj', '.tmp', 'artifacts', 'data', 'node_modules', 'publish',
    '.vs', '.vscode', '.idea', 'TestResults'
)
$script:IshWYExcludedTopLevel = @(
    'Record', 'docs', 'handoff', '.dsh-memory', '.agent-teams'
)
$script:IshWYExcludedTopLevelFiles = @(
    'README.md', 'LICENSE', 'LICENSE.txt', '.gitignore', '.gitattributes'
)
# 显式、可审查的「被忽略且非构建」后缀：截图/日志是本仓库已知的取证与噪声文件。
# 不在此清单内的后缀（含未知扩展名与无扩展名文件）默认按构建输入缺口处理，
# 由完整门禁失败关闭（B9：未知目录/未知扩展名不得静默漏过）。
$script:IshWYNonBuildIgnoredExtensions = @(
    '.png', '.jpg', '.jpeg', '.gif', '.bmp', '.webp', '.ico', '.log'
)

function Get-BuildInputExclusionReason {
    <#
    返回排除原因；$null 表示该路径应纳入构建输入指纹。
    只按路径形状判断，不读文件内容。
    #>
    param([string]$RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath)) {
        return 'empty-path'
    }
    $p = $RelativePath.Replace('\', '/')
    if ($p -match '(^|/)\.git(/|$)') {
        return 'git-metadata'
    }
    foreach ($segment in $script:IshWYExcludedSegments) {
        if ($p -match ('(^|/)' + [regex]::Escape($segment) + '(/|$)')) {
            return "generated:$segment"
        }
    }
    $top = ($p -split '/')[0]
    if ($script:IshWYExcludedTopLevel -contains $top) {
        return "non-build:$top"
    }
    if ($script:IshWYExcludedTopLevelFiles -contains $p) {
        return "non-build:$p"
    }
    if ($p -match '^README\.[A-Za-z0-9]+$' -or $p -match '^LICENSE\.[A-Za-z0-9]+$') {
        return 'non-build:readme-license'
    }
    return $null
}

function Test-BuildInputPath {
    <#
    tracked/untracked 文件是否应纳入源码指纹：默认纳入，显式排除。
    #>
    param([string]$RelativePath)
    return ($null -eq (Get-BuildInputExclusionReason $RelativePath))
}

function Test-IgnoredBuildInputCandidate {
    <#
    被 gitignore 的文件是否构成「构建输入覆盖缺口」（B9：默认纳入、显式排除）。
    - 已由生成物/非构建资料规则排除的路径不算缺口；
    - 显式非构建后缀（截图/日志）不算缺口；
    - 其余一律算缺口：未知顶层目录、未知扩展名、无扩展名文件都不会静默漏过。
    #>
    param([string]$RelativePath)

    $p = $RelativePath.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($p)) {
        return $false
    }
    if ($null -ne (Get-BuildInputExclusionReason $p)) {
        return $false
    }
    $extension = [System.IO.Path]::GetExtension($p).ToLowerInvariant()
    if ($script:IshWYNonBuildIgnoredExtensions -contains $extension) {
        return $false
    }
    return $true
}

function Test-VersionString {
    <#
    发布版本只接受 SemVer 形状：X.Y.Z 或 X.Y.Z-prerelease（点分标识）。
    数值段禁止前导零；拒绝前导 v、路径分隔符、空白、换行、分号、$、反引号等
    可能进入命令行/文件名/标签的字符（M4：01.3.1 不再被接受）。
    #>
    param([string]$Version)

    if ([string]::IsNullOrWhiteSpace($Version) -or $Version.Length -gt 64) {
        return $false
    }
    return $Version -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:\.[0-9A-Za-z]+)*)?$'
}

function Assert-VersionString {
    param([string]$Version)
    if (-not (Test-VersionString $Version)) {
        throw "版本号非法：'$Version'；只接受 X.Y.Z 或 X.Y.Z-prerelease（数值段无前导零）"
    }
}

function ConvertTo-StrictVersion {
    <#
    把 workflow 的不可信原始输入规范成严格版本：只允许规范化「一个」前导 v。
    空白/换行/控制字符/多余前缀/非法形状一律拒绝，且必须在写 GITHUB_OUTPUT 之前调用（B1）。
    #>
    param([string]$Raw)

    if ([string]::IsNullOrEmpty($Raw)) {
        throw '版本号为空'
    }
    if ($Raw -match '[\s]') {
        throw "版本号含空白或换行：'$Raw'"
    }
    if ($Raw -match '[\x00-\x1f\x7f]') {
        throw "版本号含控制字符：'$Raw'"
    }
    $value = $Raw
    if ($value.StartsWith('v')) {
        $value = $value.Substring(1)
    }
    if (-not (Test-VersionString $value)) {
        throw "版本号非法：'$Raw'；只接受 X.Y.Z 或 X.Y.Z-prerelease"
    }
    return $value
}

function Get-VersionShape {
    <#
    拆分已校验版本：Core=X.Y.Z、FileVersion=X.Y.Z.0（Win32 四段数值不含预发布后缀）。
    #>
    param([Parameter(Mandatory = $true)][string]$Version)

    Assert-VersionString $Version
    $core = ($Version -split '-')[0]
    return [pscustomobject]@{
        Version = $Version
        Core = $core
        FileVersion = "$core.0"
        Prerelease = if ($Version.Contains('-')) { $Version.Substring($core.Length + 1) } else { '' }
    }
}

function Get-NormalizedProductVersion {
    <#
    去掉 SDK/源链接可能追加的 +<build metadata>，并 Trim。
    仅用于比较；manifest 另存原始值。
    #>
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ''
    }
    $trimmed = $Value.Trim()
    $plus = $trimmed.IndexOf('+')
    if ($plus -ge 0) {
        $trimmed = $trimmed.Substring(0, $plus)
    }
    return $trimmed
}

function Test-ArtifactVersionMatch {
    <#
    产物版本必须与 manifest 版本精确一致（B2）：
    - ProductVersion（去 +metadata）== 规范化版本（含预发布后缀），不接受前缀匹配，
      因此 1.3.1 不会接受 1.3.10；
    - FileVersion == X.Y.Z.0。
    仅做字符串规则，不读文件，便于离线用例。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Expected,
        [string]$ProductVersion,
        [string]$FileVersion
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    $shape = Get-VersionShape $Expected
    $expectedProduct = Get-NormalizedProductVersion $Expected
    $actualProduct = Get-NormalizedProductVersion $ProductVersion
    if ([string]::IsNullOrWhiteSpace($actualProduct)) {
        $reasons.Add('product-version-missing')
    }
    elseif (-not [string]::Equals($actualProduct, $expectedProduct, [StringComparison]::Ordinal)) {
        $reasons.Add(("product-version-mismatch: expected '{0}', got '{1}'" -f $expectedProduct, $actualProduct))
    }
    $actualFile = Get-NormalizedProductVersion $FileVersion
    if ([string]::IsNullOrWhiteSpace($actualFile)) {
        $reasons.Add('file-version-missing')
    }
    elseif (-not [string]::Equals($actualFile, $shape.FileVersion, [StringComparison]::Ordinal)) {
        $reasons.Add(("file-version-mismatch: expected '{0}', got '{1}'" -f $shape.FileVersion, $actualFile))
    }

    return [pscustomobject]@{
        Ok = $reasons.Count -eq 0
        Reasons = @($reasons)
        ExpectedProduct = $expectedProduct
        ExpectedFile = $shape.FileVersion
    }
}

function Get-GitLines {
    param([string]$Root, [string[]]$Arguments)
    # 只取 stdout：stderr 一旦混入会被误当作文件路径。失败时以参数重试定位。
    $output = & git -C $Root @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw ("git 命令失败（exit {0}）：git {1}" -f $LASTEXITCODE, ($Arguments -join ' '))
    }
    return @($output)
}

function Get-SourceFingerprint {
    <#
    返回 Fingerprint、Inputs、ExcludedInputs、IgnoredInputs、IgnoredInputCandidates、UncoveredInputs。
    Inputs 按相对路径排序，指纹为 path<NUL>sha256<LF> 序列的 SHA256。
    #>
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootFull = ConvertTo-NormalizedPath $Root
    # core.quotepath=false：非 ASCII 路径原样输出，避免带引号/八进制转义后 Test-Path 失败。
    $files = @(Get-GitLines -Root $rootFull -Arguments @('-c', 'core.quotepath=false', 'ls-files', '-co', '--exclude-standard'))
    $ignored = @(Get-GitLines -Root $rootFull -Arguments @('-c', 'core.quotepath=false', 'ls-files', '-oi', '--exclude-standard'))

    $inputs = [System.Collections.Generic.List[object]]::new()
    $excluded = [System.Collections.Generic.List[object]]::new()
    $uncovered = [System.Collections.Generic.List[string]]::new()
    $covered = 0

    foreach ($relative in $files) {
        if ([string]::IsNullOrWhiteSpace($relative)) { continue }
        $normalized = $relative.Replace('\', '/')
        $reason = Get-BuildInputExclusionReason $normalized
        if ($null -ne $reason) {
            $excluded.Add([pscustomobject]@{ path = $normalized; reason = $reason })
            continue
        }
        $full = Join-Path $rootFull $relative
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) {
            # 形状上是构建输入但读不到（链接/权限/竞态）：不静默跳过，记为覆盖缺口。
            $uncovered.Add($normalized)
            continue
        }
        try {
            $hash = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        catch {
            $uncovered.Add($normalized)
            continue
        }
        $covered++
        $inputs.Add([pscustomobject]@{
            path = $normalized
            sha256 = $hash
            bytes = (Get-Item -LiteralPath $full).Length
        })
    }

    $sorted = @($inputs | Sort-Object -Property path -CaseSensitive:$false)
    $builder = [System.Text.StringBuilder]::new()
    foreach ($item in $sorted) {
        # 固定 LF：跨平台/跨次运行确定性，不依赖 Environment.NewLine。
        [void]$builder.Append($item.path).Append([char]0).Append($item.sha256).Append([char]10)
    }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($builder.ToString())
    $fingerprint = [System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()

    $ignoredList = @($ignored | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | ForEach-Object { $_.Replace('\', '/') })
    $ignoredCandidates = @($ignoredList | Where-Object { Test-IgnoredBuildInputCandidate $_ } | Sort-Object)

    return [pscustomobject]@{
        Fingerprint = $fingerprint
        Inputs = $sorted
        ExcludedInputs = @($excluded | Sort-Object -Property path)
        IgnoredInputs = $ignoredList
        IgnoredInputCandidates = $ignoredCandidates
        UncoveredInputs = @($uncovered | Sort-Object)
        TrackedFileCount = $covered
    }
}

function Get-SourceIdentity {
    param([Parameter(Mandatory = $true)][string]$Root)

    $rootFull = ConvertTo-NormalizedPath $Root
    $head = (@(Get-GitLines -Root $rootFull -Arguments @('rev-parse', 'HEAD'))[0]).Trim()
    $porcelain = @(Get-GitLines -Root $rootFull -Arguments @('status', '--porcelain', '--untracked-files=normal'))
    $dirtyLines = @($porcelain | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $dirty = $dirtyLines.Count -gt 0
    $fingerprint = Get-SourceFingerprint -Root $rootFull

    return [pscustomobject]@{
        Root = $rootFull
        Head = $head
        Dirty = $dirty
        DirtyFileCount = $dirtyLines.Count
        Fingerprint = $fingerprint.Fingerprint
        InputCount = @($fingerprint.Inputs).Count
        Inputs = $fingerprint.Inputs
        ExcludedInputs = $fingerprint.ExcludedInputs
        IgnoredInputs = $fingerprint.IgnoredInputs
        IgnoredInputCandidates = $fingerprint.IgnoredInputCandidates
        UncoveredInputs = $fingerprint.UncoveredInputs
    }
}

function Test-ReleaseSourceAllowed {
    <#
    release 模式要求工作树干净；ci 模式允许 dirty，但 manifest 必须记录。
    返回 $null 表示允许，否则返回失败原因码。
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][bool]$Dirty
    )
    if ($Mode -eq 'release' -and $Dirty) {
        return 'release-refuses-dirty-source'
    }
    return $null
}

function Get-EnvironmentIdentity {
    param([string]$DotnetPath = 'dotnet')

    $dotnetVersion = ''
    try {
        $dotnetVersion = (& $DotnetPath --version 2>$null | Select-Object -First 1)
    }
    catch {
        $dotnetVersion = 'unavailable'
    }

    return [pscustomobject]@{
        os = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        pwsh = $PSVersionTable.PSVersion.ToString()
        dotnet = "$dotnetVersion".Trim()
        culture = [System.Globalization.CultureInfo]::CurrentCulture.Name
    }
}
