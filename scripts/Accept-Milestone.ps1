[CmdletBinding()]
param(
    [ValidateSet('M0', 'M1', 'M2', 'M3', 'M4', 'M5')]
    [string]$Milestone = 'M5',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$AllowRealCursorHide,
    [string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptRoot
. (Join-Path $scriptRoot 'Get-DotNet.ps1')
. (Join-Path $scriptRoot 'FoxMouse.ScriptSafety.ps1')
$dotnet = Get-FoxMouseDotNet
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$currentVersion = [string]$buildProperties.Project.PropertyGroup.Version
if ($currentVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props must define a three-part numeric Version; found '$currentVersion'."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "ExpectedVersion must be a three-part numeric version; found '$ExpectedVersion'."
    }
    if (-not [string]::Equals($ExpectedVersion, $currentVersion, [StringComparison]::Ordinal)) {
        throw "Acceptance version mismatch: expected '$ExpectedVersion', but Directory.Build.props defines '$currentVersion'."
    }
}
$ExpectedVersion = $currentVersion
$runId = "{0}-{1}" -f $Milestone.ToLowerInvariant(), (Get-Date -Format 'yyyyMMdd-HHmmss')
$evidenceRoot = Join-Path $projectRoot "artifacts\validation\$runId"
New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
$evidenceUtf8 = New-Object Text.UTF8Encoding($false)

function Set-EvidenceLog {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [string[]]$Lines = @()
    )

    $text = if ($Lines.Count -eq 0) {
        [string]::Empty
    }
    else {
        ($Lines -join [Environment]::NewLine) + [Environment]::NewLine
    }
    [IO.File]::WriteAllText($LiteralPath, $text, $evidenceUtf8)
}

function Add-EvidenceLogLine {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [AllowNull()]
        [object]$Value
    )

    [IO.File]::AppendAllText(
        $LiteralPath,
        ([string]$Value) + [Environment]::NewLine,
        $evidenceUtf8)
}

function Write-EvidenceLog {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [Parameter(ValueFromPipeline = $true)]
        [AllowNull()]
        [object]$InputObject
    )

    process {
        Add-EvidenceLogLine -LiteralPath $LiteralPath -Value $InputObject
        Write-Output $InputObject
    }
}

function Invoke-GateCommand {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $logPath = Join-Path $evidenceRoot "$Name.log"
    Set-EvidenceLog -LiteralPath $logPath -Lines @(
        "startedUtc=$([DateTimeOffset]::UtcNow.ToString('O'))"
        "version=$ExpectedVersion"
        "executable=$dotnet"
        "arguments=$($Arguments | ConvertTo-Json -Compress)"
    )
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell promotes native stderr redirected with 2>&1 to an
        # ErrorRecord. The process exit code remains the authoritative gate.
        $ErrorActionPreference = 'Continue'
        & $dotnet @Arguments 2>&1 | Write-EvidenceLog -LiteralPath $logPath
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    Add-EvidenceLogLine -LiteralPath $logPath -Value "exitCode=$exitCode"
    if ($exitCode -ne 0) {
        throw "Gate command '$Name' failed with exit code $exitCode."
    }
}

function Invoke-GateExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$Executable,
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $logPath = Join-Path $evidenceRoot "$Name.log"
    Set-EvidenceLog -LiteralPath $logPath -Lines @(
        "startedUtc=$([DateTimeOffset]::UtcNow.ToString('O'))"
        "version=$ExpectedVersion"
        "executable=$Executable"
        "arguments=$($Arguments | ConvertTo-Json -Compress)"
    )
    $startInfo = New-FoxMouseProcessStartInfo `
        -Executable $Executable `
        -ArgumentList $Arguments `
        -Hidden
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            [void]$process.WaitForExit(5000)
            throw "Gate executable '$Name' timed out."
        }
        $exitCode = $process.ExitCode
    }
    finally {
        $process.Dispose()
    }

    Add-EvidenceLogLine -LiteralPath $logPath -Value "exitCode=$exitCode"
    if ($exitCode -ne 0) {
        throw "Gate executable '$Name' failed with exit code $exitCode."
    }
}

$rank = @{ M0 = 0; M1 = 1; M2 = 2; M3 = 3; M4 = 4; M5 = 5 }
$targetRank = $rank[$Milestone]

Invoke-GateCommand -Name 'restore' -Arguments @('restore', (Join-Path $projectRoot 'FoxMouse.slnx'), '--nologo')
Invoke-GateCommand -Name 'build' -Arguments @('build', (Join-Path $projectRoot 'FoxMouse.slnx'), '-c', $Configuration, '--no-restore', '--nologo')
Invoke-GateCommand -Name 'tests' -Arguments @('test', (Join-Path $projectRoot 'FoxMouse.slnx'), '-c', $Configuration, '--no-build', '--nologo')

if ($targetRank -ge 1) {
    $traceTool = Join-Path $projectRoot "src\FoxMouse.TraceTool\bin\$Configuration\net10.0\FoxMouse.TraceTool.dll"
    Invoke-GateCommand -Name 'trace-verify' -Arguments @($traceTool, 'verify', (Join-Path $projectRoot 'tests\data'))
}

if ($targetRank -ge 3) {
    $deploymentSafetyLog = Join-Path $evidenceRoot 'deployment-safety.log'
    Set-EvidenceLog -LiteralPath $deploymentSafetyLog
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & (Join-Path $scriptRoot 'Test-DeploymentSafety.ps1') 2>&1 |
            Write-EvidenceLog -LiteralPath $deploymentSafetyLog
    }
    catch {
        Add-EvidenceLogLine -LiteralPath $deploymentSafetyLog -Value $_
        throw 'Deployment safety checks failed.'
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    Invoke-GateCommand -Name 'installer-lifecycle' -Arguments @(
        'test',
        (Join-Path $projectRoot 'tests\FoxMouse.Deployment.Tests\FoxMouse.Deployment.Tests.csproj'),
        '-c', $Configuration,
        '--no-build',
        '--nologo',
        '--logger', 'console;verbosity=detailed')

    $guard = Get-ChildItem -Path (Join-Path $projectRoot "src\FoxMouse.Guard\bin\$Configuration") -Filter 'FoxMouse.Guard.dll' -Recurse |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($guard)) {
        throw 'FoxMouse.Guard build output was not found.'
    }

    Invoke-GateCommand -Name 'guard-fake-smoke' -Arguments @($guard, '--self-test')

    $settings = Get-ChildItem -Path (Join-Path $projectRoot "src\FoxMouse.Settings\bin\$Configuration") -Filter 'FoxMouse.Settings.exe' -Recurse |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($settings)) {
        throw 'FoxMouse.Settings build output was not found.'
    }

    Invoke-GateExecutable -Name 'settings-smoke' -Executable $settings -Arguments @('--smoke-test')
    Invoke-GateExecutable -Name 'settings-general-window-smoke' -Executable $settings -Arguments @('--window-smoke-test')
    Invoke-GateExecutable -Name 'settings-exclusions-window-smoke' -Executable $settings -Arguments @('--window-smoke-test', '--page=exclusions')
    Invoke-GateExecutable -Name 'settings-about-window-smoke' -Executable $settings -Arguments @('--window-smoke-test', '--page=about')
}

if ($targetRank -ge 4) {
    $environmentLog = Join-Path $evidenceRoot 'environment.log'
    Set-EvidenceLog -LiteralPath $environmentLog
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & (Join-Path $scriptRoot 'Write-ValidationEnvironment.ps1') `
            -OutputPath (Join-Path $evidenceRoot 'environment.json') 2>&1 |
            Write-EvidenceLog -LiteralPath $environmentLog
    }
    catch {
        Add-EvidenceLogLine -LiteralPath $environmentLog -Value $_
        throw 'Validation environment capture failed.'
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    Copy-Item `
        -LiteralPath (Join-Path $projectRoot 'docs\validation\display-matrix-checklist.md') `
        -Destination (Join-Path $evidenceRoot 'display-matrix-checklist.md') `
        -Force

    Invoke-GateCommand -Name 'overlay-performance' -Arguments @(
        'test',
        (Join-Path $projectRoot 'tests\FoxMouse.Platform.Windows.Tests\FoxMouse.Platform.Windows.Tests.csproj'),
        '-c', $Configuration,
        '--no-build',
        '--nologo',
        '--filter', 'Category=Performance',
        '--logger', 'console;verbosity=detailed')

    $app = Get-ChildItem -Path (Join-Path $projectRoot "src\FoxMouse.App\bin\$Configuration") -Filter 'FoxMouse.dll' -Recurse |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($app)) {
        throw 'FoxMouse.App build output was not found.'
    }

    Invoke-GateCommand -Name 'lifecycle-smoke' -Arguments @($app, '--lifecycle-smoke')
    Invoke-GateCommand -Name 'tray-menu-smoke' -Arguments @($app, '--tray-menu-smoke')
    $smokeArguments = @($app, '--smoke-test')
    if (-not $AllowRealCursorHide) {
        Invoke-GateCommand -Name 'interactive-smoke' -Arguments $smokeArguments
    }
    else {
        # A standalone recovery call brackets the Guard-owned real-hide smoke.
        # The postcondition runs even when the smoke process fails.
        Invoke-GateCommand -Name 'cursor-pre-restore' -Arguments @($guard, '--restore-cursor')
        try {
            Invoke-GateCommand -Name 'interactive-smoke-real-hide' -Arguments @(
                $app,
                '--smoke-test',
                '--allow-real-cursor-hide')
        }
        finally {
            Invoke-GateCommand -Name 'cursor-post-restore' -Arguments @($guard, '--restore-cursor')
        }
    }
}

if ($targetRank -ge 5) {
    $releaseBuildLog = Join-Path $evidenceRoot 'release-build.log'
    Set-EvidenceLog -LiteralPath $releaseBuildLog
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & (Join-Path $scriptRoot 'New-Release.ps1') `
            -Configuration $Configuration `
            -ExpectedVersion $ExpectedVersion `
            -SkipValidation 2>&1 |
            Write-EvidenceLog -LiteralPath $releaseBuildLog
        Add-EvidenceLogLine -LiteralPath $releaseBuildLog -Value 'exitCode=0'
    }
    catch {
        Add-EvidenceLogLine -LiteralPath $releaseBuildLog -Value $_
        Add-EvidenceLogLine -LiteralPath $releaseBuildLog -Value 'exitCode=1'
        throw 'Release packaging failed.'
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    $releaseArtifactLog = Join-Path $evidenceRoot 'release-artifact.log'
    Set-EvidenceLog -LiteralPath $releaseArtifactLog
    try {
        $previousErrorActionPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & (Join-Path $scriptRoot 'Test-ReleaseArtifact.ps1') `
            -ReleaseRoot (Join-Path $projectRoot "artifacts\release\v$ExpectedVersion") `
            -ExpectedVersion $ExpectedVersion `
            -EvidenceRoot $evidenceRoot `
            -AllowRealCursorHide:$AllowRealCursorHide 2>&1 |
            Write-EvidenceLog -LiteralPath $releaseArtifactLog
    }
    catch {
        Add-EvidenceLogLine -LiteralPath $releaseArtifactLog -Value $_
        throw 'Release artifact verification failed.'
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

$windowCaptures = @(
    Get-ChildItem -LiteralPath $evidenceRoot -Filter '*-maintenance-*.png' -File -ErrorAction SilentlyContinue |
        Sort-Object Name |
        Select-Object -ExpandProperty Name
)
$windowMetrics = @(
    Get-ChildItem -LiteralPath $evidenceRoot -Filter '*-maintenance-*.json' -File -ErrorAction SilentlyContinue |
        Sort-Object Name |
        Select-Object -ExpandProperty Name
)
if ($targetRank -ge 5 -and [Version]$ExpectedVersion -ge [Version]'0.4.2') {
    foreach ($requiredEvidencePattern in @(
        'setup-maintenance-idle-*.png',
        'setup-maintenance-idle-*.json',
        'root-maintenance-installed-*.png',
        'root-maintenance-installed-*.json',
        'root-maintenance-uninstall-confirmation-*.png',
        'root-maintenance-uninstall-confirmation-*.json')) {
        if (@(Get-ChildItem -LiteralPath $evidenceRoot -Filter $requiredEvidencePattern -File -ErrorAction SilentlyContinue).Count -ne 1) {
            throw "M5 requires exactly one v0.4.2 window-evidence file matching '$requiredEvidencePattern'."
        }
    }
}
$historicalReleases = @(
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'artifacts\release') -Directory -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -match '^v\d+\.\d+\.\d+$' -and
            -not [string]::Equals($_.Name, "v$ExpectedVersion", [StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object Name |
        Select-Object -ExpandProperty Name
)
$summary = [ordered]@{
    schema = 'foxmouse.validation/1'
    version = $ExpectedVersion
    milestone = $Milestone
    configuration = $Configuration
    status = 'PASS'
    allowRealCursorHide = [bool]$AllowRealCursorHide
    completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    evidence = $evidenceRoot
    windowCaptures = $windowCaptures
    windowMetrics = $windowMetrics
    preservedHistoricalReleases = $historicalReleases
}
[IO.File]::WriteAllText(
    (Join-Path $evidenceRoot 'summary.json'),
    ($summary | ConvertTo-Json),
    $evidenceUtf8)
$summary | Format-List
