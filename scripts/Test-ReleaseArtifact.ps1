[CmdletBinding()]
param(
    [string]$ReleaseRoot,
    [string]$ExpectedVersion,
    [string]$EvidenceRoot,
    [switch]$AllowRealCursorHide,
    [switch]$Candidate
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptRoot
. (Join-Path $scriptRoot 'FoxMouse.ScriptSafety.ps1')

if ([string]::IsNullOrWhiteSpace($ReleaseRoot)) {
    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
    $currentVersion = [string]$buildProperties.Project.PropertyGroup.Version
    $ReleaseRoot = Join-Path $projectRoot "artifacts\release\v$currentVersion"
}

$artifactsRoot = Get-FoxMouseFullPath -LiteralPath (Join-Path $projectRoot 'artifacts')
$releaseParent = Get-FoxMouseFullPath -LiteralPath (Join-Path $artifactsRoot 'release')
if ($Candidate) {
    $releaseRootPath = Assert-FoxMouseDirectoryRoot `
        -LiteralPath $ReleaseRoot `
        -ExpectedParent $artifactsRoot `
        -Label 'candidate release verification root'
    $candidatePattern = [Regex]::Escape($artifactsRoot + [IO.Path]::DirectorySeparatorChar) +
        '\.FoxMouse\.release-staging\.[0-9a-fA-F]{32}\\release-output$'
    if ($releaseRootPath -notmatch $candidatePattern) {
        throw "Candidate release root is outside the transaction layout: $releaseRootPath"
    }
}
else {
    $releaseRootPath = Assert-FoxMouseDirectoryRoot `
        -LiteralPath $ReleaseRoot `
        -ExpectedParent $releaseParent `
        -Label 'release verification root'
}
$null = Assert-FoxMouseTreeHasNoReparsePoints `
    -LiteralPath $releaseRootPath `
    -Label 'release verification root'

$manifestPath = Assert-FoxMouseFile `
    -LiteralPath (Join-Path $releaseRootPath 'release-manifest.json') `
    -ExpectedParent $releaseRootPath `
    -Label 'release manifest'
$manifest = Get-Content -LiteralPath $manifestPath -Raw -ErrorAction Stop | ConvertFrom-Json
if ([string]$manifest.version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Release manifest has an invalid three-part version: '$($manifest.version)'."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "ExpectedVersion must be a three-part numeric version; found '$ExpectedVersion'."
    }
    if (-not [string]::Equals([string]$manifest.version, $ExpectedVersion, [StringComparison]::Ordinal)) {
        throw "Release manifest version '$($manifest.version)' does not match expected version '$ExpectedVersion'."
    }
}
if ($manifest.schema -ne 'foxmouse.release/2' -or
    $manifest.product -ne 'FoxMouse' -or
    $manifest.runtime -ne 'win-x64' -or
    $manifest.installer -ne 'self-contained-dotnet' -or
    'repair' -notin @($manifest.maintenance) -or
    'uninstall' -notin @($manifest.maintenance) -or
    'arp-hkcu' -notin @($manifest.maintenance) -or
    'root-uninstaller' -notin @($manifest.maintenance) -or
    'quiet-maintenance-host' -notin @($manifest.maintenance) -or
    'detached-handoff' -notin @($manifest.maintenance) -or
    'constrained-cleanup-worker' -notin @($manifest.maintenance) -or
    'candidate-gated' -notin @($manifest.maintenance)) {
    throw 'Release manifest identity is invalid.'
}
if ([Version]$manifest.version -ge [Version]'0.4.2') {
    foreach ($requiredCapability in @(
        'custom-install-parent',
        'single-page-maintenance',
        'no-scroll-maintenance')) {
        if ($requiredCapability -notin @($manifest.maintenance)) {
            throw "Release manifest is missing the v0.4.2 capability '$requiredCapability'."
        }
    }
}
if (-not $Candidate -and
    -not [string]::Equals(
        (Split-Path -Leaf $releaseRootPath),
        "v$($manifest.version)",
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release directory '$releaseRootPath' does not match manifest version '$($manifest.version)'."
}

foreach ($artifactRecord in @($manifest.artifacts)) {
    $artifactPath = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $releaseRootPath $artifactRecord.file) `
        -ExpectedParent $releaseRootPath `
        -Label 'release artifact'
    $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne ([string]$artifactRecord.sha256).ToLowerInvariant()) {
        throw "SHA-256 mismatch for '$artifactPath'."
    }

    if ((Get-Item -LiteralPath $artifactPath).Length -ne [long]$artifactRecord.bytes) {
        throw "Byte-length mismatch for '$artifactPath'."
    }
}

$portable = Assert-FoxMouseFile `
    -LiteralPath (Join-Path $releaseRootPath "FoxMouse-v$($manifest.version)-win-x64.zip") `
    -ExpectedParent $releaseRootPath `
    -Label 'portable ZIP'
$setup = Assert-FoxMouseFile `
    -LiteralPath (Join-Path $releaseRootPath 'FoxMouse-Setup-x64.exe') `
    -ExpectedParent $releaseRootPath `
    -Label 'setup executable'

# A developer may verify a package while an installed FoxMouse instance is
# already running. Preserve that baseline and only reject processes leaked by
# this verification run.
$preexistingProductProcessIds = @(
    Get-Process -Name 'FoxMouse', 'FoxMouse.Guard', 'FoxMouse.Settings' -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty Id
)

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($portable)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    foreach ($entryName in $entryNames) {
        if ($entryName.StartsWith('/', [StringComparison]::Ordinal) -or
            $entryName -match '(^|/)\.\.(/|$)' -or
            -not $entryName.StartsWith('FoxMouse/', [StringComparison]::Ordinal)) {
            throw "Unsafe or unexpected ZIP entry: $entryName"
        }
    }

    foreach ($requiredEntry in @(
        'FoxMouse/FoxMouse.exe',
        'FoxMouse/FoxMouse.Guard.exe',
        'FoxMouse/FoxMouse.Uninstall.exe',
        'FoxMouse/FoxMouse.Cleanup.exe',
        'FoxMouse/Settings/FoxMouse.Settings.exe',
        'FoxMouse/Settings/FoxMouse.Settings.pri',
        'FoxMouse/Settings/App.xbf',
        'FoxMouse/Settings/MainWindow.xbf',
        'FoxMouse/Settings/Pages/GeneralPage.xbf',
        'FoxMouse/Settings/Pages/ExclusionsPage.xbf',
        'FoxMouse/Settings/Pages/AboutPage.xbf',
        'FoxMouse/README.md')) {
        if ($requiredEntry -notin $entryNames) {
            throw "Portable ZIP is missing '$requiredEntry'."
        }
    }


    foreach ($forbiddenEntry in @(
        'FoxMouse/FoxMouse.ScriptSafety.ps1',
        'FoxMouse/Uninstall-FoxMouse.ps1')) {
        if ($forbiddenEntry -in $entryNames) {
            throw "Portable ZIP contains retired maintenance entry '$forbiddenEntry'."
        }
    }
}
finally {
    $archive.Dispose()
}

$tempRoot = Get-FoxMouseFullPath -LiteralPath ([IO.Path]::GetTempPath())
$null = Assert-FoxMouseDirectoryRoot -LiteralPath $tempRoot -Label 'temporary root'
$runRoot = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $tempRoot ("FoxMouse-ReleaseVerify-" + [Guid]::NewGuid().ToString('N'))) `
    -ExpectedParent $tempRoot `
    -Label 'release verification temporary root'
[void][IO.Directory]::CreateDirectory($runRoot)
$null = Assert-FoxMouseDirectoryRoot `
    -LiteralPath $runRoot `
    -ExpectedParent $tempRoot `
    -Label 'release verification temporary root'

function Invoke-FoxMouseVerifiedProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,
        [Parameter(Mandatory)]
        [string[]]$ArgumentList,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $executablePath = Assert-FoxMouseFile -LiteralPath $Executable -Label $Label
    $startInfo = New-FoxMouseProcessStartInfo `
        -Executable $executablePath `
        -ArgumentList $ArgumentList `
        -Hidden
    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        if (-not $process.WaitForExit(15000)) {
            $process.Kill()
            [void]$process.WaitForExit(5000)
            throw "$Label timed out."
        }

        if ($process.ExitCode -ne 0) {
            throw "$Label failed with exit code $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }
}

try {
    $portableExtract = Join-Path $runRoot 'portable'
    [void][IO.Directory]::CreateDirectory($portableExtract)
    Expand-Archive -LiteralPath $portable -DestinationPath $portableExtract -Force
    $null = Assert-FoxMouseTreeHasNoReparsePoints `
        -LiteralPath $portableExtract `
        -Label 'portable extraction'
    $portableProduct = Assert-FoxMouseDirectoryRoot `
        -LiteralPath (Join-Path $portableExtract 'FoxMouse') `
        -ExpectedParent $portableExtract `
        -Label 'portable product'
    $portableApp = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $portableProduct 'FoxMouse.exe') `
        -ExpectedParent $portableProduct `
        -Label 'portable FoxMouse.exe'
    $portableGuard = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $portableProduct 'FoxMouse.Guard.exe') `
        -ExpectedParent $portableProduct `
        -Label 'portable FoxMouse.Guard.exe'
    $portableSettingsRoot = Assert-FoxMouseDirectoryRoot `
        -LiteralPath (Join-Path $portableProduct 'Settings') `
        -ExpectedParent $portableProduct `
        -Label 'portable Settings directory'
    $portableSettings = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $portableSettingsRoot 'FoxMouse.Settings.exe') `
        -ExpectedParent $portableSettingsRoot `
        -Label 'portable FoxMouse.Settings.exe'
    $portableUninstaller = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $portableProduct 'FoxMouse.Uninstall.exe') `
        -ExpectedParent $portableProduct `
        -Label 'portable FoxMouse.Uninstall.exe'
    $portableCleanup = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $portableProduct 'FoxMouse.Cleanup.exe') `
        -ExpectedParent $portableProduct `
        -Label 'portable FoxMouse.Cleanup.exe'

    $maintenanceContractPath = Join-Path $runRoot 'maintenance-contract.json'
    $maintenanceStartInfo = New-FoxMouseProcessStartInfo `
        -Executable $portableUninstaller `
        -ArgumentList @('--write-contract', $maintenanceContractPath) `
        -Hidden
    $maintenanceProcess = [Diagnostics.Process]::Start($maintenanceStartInfo)
    if (-not $maintenanceProcess.WaitForExit(15000)) {
        $maintenanceProcess.Kill()
        [void]$maintenanceProcess.WaitForExit(5000)
        throw 'Root maintenance executable contract check timed out.'
    }
    $maintenanceExitCode = $maintenanceProcess.ExitCode
    $maintenanceProcess.Dispose()
    if ($maintenanceExitCode -ne 0) {
        throw "Root maintenance executable contract check failed with exit code $maintenanceExitCode."
    }

    $maintenanceContract = Get-Content -LiteralPath $maintenanceContractPath -Raw | ConvertFrom-Json
    if ($maintenanceContract.schema -ne 'foxmouse.maintenance/1' -or
        -not [bool]$maintenanceContract.graphical -or
        'repair' -notin @($maintenanceContract.verbs) -or
        'uninstall' -notin @($maintenanceContract.verbs) -or
        $maintenanceContract.arpScope -ne 'HKCU' -or
        -not [bool]$maintenanceContract.detachedHandshake -or
        $maintenanceContract.rootLauncherCompletion -ne 'asynchronous-result-file' -or
        $maintenanceContract.quietExitCode -ne 'synchronous-maintenance-host' -or
        $maintenanceContract.cleanup -ne 'constrained-worker') {
        throw 'Root maintenance executable contract is invalid.'
    }

    $setupVersion = (Get-Item -LiteralPath $setup).VersionInfo.ProductVersion
    $uninstallerVersion = (Get-Item -LiteralPath $portableUninstaller).VersionInfo.ProductVersion
    $cleanupVersion = (Get-Item -LiteralPath $portableCleanup).VersionInfo.ProductVersion
    if ($setupVersion -notlike "$($manifest.version)*" -or
        $uninstallerVersion -notlike "$($manifest.version)*" -or
        $cleanupVersion -notlike "$($manifest.version)*") {
        throw "Setup, root uninstaller, or cleanup worker version does not match release $($manifest.version)."
    }
    if ((Get-Item -LiteralPath $portableCleanup).Length -gt 1048576) {
        throw 'Portable cleanup worker unexpectedly exceeds 1 MiB.'
    }
    Add-Type -AssemblyName System.Drawing
    $cleanupIcon = [Drawing.Icon]::ExtractAssociatedIcon($portableCleanup)
    try {
        if ($null -eq $cleanupIcon -or $cleanupIcon.Width -lt 16 -or $cleanupIcon.Height -lt 16) {
            throw 'Portable cleanup worker has no usable branded executable icon.'
        }
    }
    finally {
        if ($null -ne $cleanupIcon) {
            $cleanupIcon.Dispose()
        }
    }

    if ($AllowRealCursorHide) {
        Invoke-FoxMouseVerifiedProcess `
            -Executable $portableGuard `
            -ArgumentList @('--restore-cursor') `
            -Label 'portable cursor pre-restore'
        try {
            Invoke-FoxMouseVerifiedProcess `
                -Executable $portableApp `
                -ArgumentList @('--smoke-test', '--allow-real-cursor-hide') `
                -Label 'portable real-hide smoke test'
        }
        finally {
            Invoke-FoxMouseVerifiedProcess `
                -Executable $portableGuard `
                -ArgumentList @('--restore-cursor') `
                -Label 'portable cursor post-restore'
        }
    }
    else {
        Invoke-FoxMouseVerifiedProcess `
            -Executable $portableApp `
            -ArgumentList @('--smoke-test') `
            -Label 'portable smoke test'
    }
    Invoke-FoxMouseVerifiedProcess `
        -Executable $portableApp `
        -ArgumentList @('--lifecycle-smoke') `
        -Label 'portable lifecycle smoke test'
    Invoke-FoxMouseVerifiedProcess `
        -Executable $portableApp `
        -ArgumentList @('--tray-menu-smoke') `
        -Label 'portable tray menu smoke test'
    Invoke-FoxMouseVerifiedProcess `
        -Executable $portableSettings `
        -ArgumentList @('--smoke-test') `
        -Label 'portable Settings smoke test'
    Invoke-FoxMouseVerifiedProcess `
        -Executable $portableSettings `
        -ArgumentList @('--window-smoke-test') `
        -Label 'portable Settings General window smoke test'
    Invoke-FoxMouseVerifiedProcess `
        -Executable $portableSettings `
        -ArgumentList @('--window-smoke-test', '--page=exclusions') `
        -Label 'portable Settings Exclusions window smoke test'
    Invoke-FoxMouseVerifiedProcess `
        -Executable $portableSettings `
        -ArgumentList @('--window-smoke-test', '--page=about') `
        -Label 'portable Settings About window smoke test'

    $setupExtract = Join-Path $runRoot 'setup'
    [void][IO.Directory]::CreateDirectory($setupExtract)
    $embeddedPackage = Join-Path $setupExtract "FoxMouse-v$($manifest.version)-win-x64.zip"
    $setupStartInfo = New-FoxMouseProcessStartInfo `
        -Executable $setup `
        -ArgumentList @('--extract-package', $embeddedPackage) `
        -Hidden
    $setupProcess = [Diagnostics.Process]::Start($setupStartInfo)
    if (-not $setupProcess.WaitForExit(30000)) {
        $setupProcess.Kill()
        [void]$setupProcess.WaitForExit(5000)
        throw 'Setup embedded-package verification timed out.'
    }
    $setupExitCode = $setupProcess.ExitCode
    $setupProcess.Dispose()
    if ($setupExitCode -ne 0) {
        throw "Setup embedded-package extraction failed with exit code $setupExitCode."
    }

    $embeddedPackage = Assert-FoxMouseFile `
        -LiteralPath $embeddedPackage `
        -ExpectedParent $setupExtract `
        -Label 'Setup embedded FoxMouse package'
    if ((Get-FileHash -LiteralPath $embeddedPackage -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $portable -Algorithm SHA256).Hash) {
        throw 'Setup embedded package does not match the portable release payload.'
    }
}
finally {
    if (Test-Path -LiteralPath $runRoot) {
        Remove-FoxMouseTree `
            -LiteralPath $runRoot `
            -ExpectedParent $tempRoot `
            -Label 'release verification temporary root'
    }
}

& (Join-Path $scriptRoot 'Test-InstallerLifecycle.ps1') `
    -ReleaseRoot $releaseRootPath `
    -ExpectedVersion ([string]$manifest.version) `
    -EvidenceRoot $EvidenceRoot
if (-not $?) {
    throw 'Isolated installer lifecycle verification failed.'
}

$remaining = @(
    Get-Process -Name 'FoxMouse', 'FoxMouse.Guard', 'FoxMouse.Settings' -ErrorAction SilentlyContinue |
        Where-Object { $_.Id -notin $preexistingProductProcessIds }
)
if ($remaining.Count -ne 0) {
    throw 'Release verification left FoxMouse processes running.'
}

Write-Output "FoxMouse release artifacts verified: $releaseRootPath"
