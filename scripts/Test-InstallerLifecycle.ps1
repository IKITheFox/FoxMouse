[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ReleaseRoot,
    [string]$ExpectedVersion,
    [string]$PackagePath,
    [string]$EvidenceRoot,
    [ValidateRange(5, 600)]
    [int]$ProcessTimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
$scriptRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $scriptRoot))
. (Join-Path $scriptRoot 'FoxMouse.ScriptSafety.ps1')
Add-Type -AssemblyName System.Drawing

$artifactsRoot = Get-FoxMouseFullPath -LiteralPath (Join-Path $projectRoot 'artifacts')
$release = Assert-FoxMouseDirectoryRoot `
    -LiteralPath $ReleaseRoot `
    -ExpectedParent $artifactsRoot `
    -Label 'installer lifecycle release root'
$setup = Assert-FoxMouseFile `
    -LiteralPath (Join-Path $release 'FoxMouse-Setup-x64.exe') `
    -ExpectedParent $release `
    -Label 'installer lifecycle Setup'
$externalPackage = $null
if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
    $externalPackage = Assert-FoxMouseFile -LiteralPath $PackagePath -ExpectedParent $artifactsRoot -Label 'online lifecycle package'
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "ExpectedVersion must be a three-part numeric version; found '$ExpectedVersion'."
    }
    $setupProductVersion = (Get-Item -LiteralPath $setup).VersionInfo.ProductVersion
    if ($setupProductVersion -notlike "$ExpectedVersion*") {
        throw "Installer lifecycle Setup version '$setupProductVersion' does not match expected version '$ExpectedVersion'."
    }
}

$testId = [Guid]::NewGuid().ToString('N')
$tempRoot = Get-FoxMouseFullPath -LiteralPath ([IO.Path]::GetTempPath())
$testRoot = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $tempRoot "FoxMouse-InstallerLifecycle-$testId") `
    -ExpectedParent $tempRoot `
    -Label 'isolated installer lifecycle root'
$captureRoot = $testRoot
if (-not [string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $validationRoot = Get-FoxMouseFullPath -LiteralPath (Join-Path $artifactsRoot 'validation')
    $captureRoot = Assert-FoxMouseDirectoryRoot `
        -LiteralPath $EvidenceRoot `
        -ExpectedParent $validationRoot `
        -Label 'installer lifecycle evidence root'
}
$registryRoot = "Software\FoxMouse\Tests\InstallerLifecycle-$testId"
$requireCustomInstallParent = [string]::IsNullOrWhiteSpace($ExpectedVersion) -or
    [Version]$ExpectedVersion -ge [Version]'0.4.2'
$defaultInstallRoot = Join-Path $testRoot 'Programs\FoxMouse'
$installParent = Join-Path $testRoot 'Selected Install Parent'
$installRoot = if ($requireCustomInstallParent) {
    Join-Path $installParent 'FoxMouse'
}
else {
    $defaultInstallRoot
}
$installParentSentinel = Join-Path $installParent 'sibling-must-survive.txt'
$settingsRoot = Join-Path $testRoot 'LocalAppData\FoxMouse'
$maintenanceHost = Join-Path $settingsRoot 'Maintenance\FoxMouse.Uninstall.exe'
$shortcutPath = Join-Path $testRoot 'StartMenu\FoxMouse.lnk'
$arpPath = "HKCU:\$registryRoot\Uninstall\FoxMouse"
$resultPath = Join-Path $tempRoot "FoxMouse-InstallerLifecycle-Result-$testId.json"
$repairResultPath = Join-Path $tempRoot "FoxMouse-InstallerLifecycle-Result-$testId-repair.json"
$quietResultPath = Join-Path $tempRoot "FoxMouse-InstallerLifecycle-Result-$testId-quiet.json"
$deleteResultPath = Join-Path $tempRoot "FoxMouse-InstallerLifecycle-Result-$testId-delete.json"
$maliciousWorkerRoot = Join-Path $tempRoot ("FoxMouse-CleanupWorker-" + [Guid]::NewGuid().ToString('N'))
$setupWindowCapture = Join-Path $captureRoot "setup-maintenance-idle-$testId.png"
$setupWindowMetrics = Join-Path $captureRoot "setup-maintenance-idle-$testId.json"
$rootWindowCapture = Join-Path $captureRoot "root-maintenance-installed-$testId.png"
$rootWindowMetrics = Join-Path $captureRoot "root-maintenance-installed-$testId.json"
$confirmationWindowCapture = Join-Path $captureRoot "root-maintenance-uninstall-confirmation-$testId.png"
$confirmationWindowMetrics = Join-Path $captureRoot "root-maintenance-uninstall-confirmation-$testId.json"

function Invoke-LifecycleExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,
        [Parameter(Mandatory)]
        [string[]]$Arguments,
        [Parameter(Mandatory)]
        [string]$Label,
        [int]$ExpectedExitCode = 0
    )

    if ($Executable -eq $setup -and $null -ne $externalPackage) {
        $Arguments = @($Arguments) + @('--package', $externalPackage)
    }
    $completionPath = $null
    if ([IO.Path]::GetFileName($Executable) -eq 'FoxMouse.Uninstall.exe') {
        $resultIndex = [Array]::IndexOf($Arguments, '--result-file')
        if ($resultIndex -ge 0) {
            $completionPath = $Arguments[$resultIndex + 1]
        } else {
            $completionPath = Join-Path $testRoot ('maintenance-completion-' + [Guid]::NewGuid().ToString('N') + '.json')
            $Arguments = @($Arguments) + @('--result-file', $completionPath)
        }
    }
    $startInfo = New-FoxMouseProcessStartInfo `
        -Executable $Executable `
        -ArgumentList $Arguments `
        -Hidden
    if ($Arguments -contains '--window-smoke-test') {
        # A visual gate must launch a visible window. SW_HIDE can suppress native
        # painting while managed Visible/layout properties still report success.
        $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
    }

    $process = [Diagnostics.Process]::Start($startInfo)
    try {
        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        if (-not $process.WaitForExit($ProcessTimeoutSeconds * 1000)) {
            $process.Kill()
            [void]$process.WaitForExit(5000)
            throw "$Label did not exit within $ProcessTimeoutSeconds seconds."
        }
        $stopwatch.Stop()

        $launcherExpectedExit = if ($null -ne $completionPath) { 0 } else { $ExpectedExitCode }
        if ($process.ExitCode -ne $launcherExpectedExit) {
            throw "$Label returned $($process.ExitCode); expected $ExpectedExitCode."
        }
        if ($null -ne $completionPath) {
            $completionDeadline = [DateTime]::UtcNow.AddSeconds($ProcessTimeoutSeconds)
            while (!(Test-Path -LiteralPath $completionPath) -and [DateTime]::UtcNow -lt $completionDeadline) {
                Start-Sleep -Milliseconds 100
            }
            if (!(Test-Path -LiteralPath $completionPath)) { throw "$Label detached host did not complete." }
            $completion = Get-Content -LiteralPath $completionPath -Raw | ConvertFrom-Json
            if ($completion.exitCode -ne $ExpectedExitCode) { throw "$Label detached host failed: $($completion.operationError)" }
        }
        Write-Output "$Label exited with code $($process.ExitCode) after $($stopwatch.ElapsedMilliseconds) ms."
    }
    finally {
        $process.Dispose()
    }
}

function Get-LifecycleArguments {
    param(
        [string[]]$Prefix,
        [switch]$IncludeInstallParent
    )

    $arguments = @($Prefix) + @(
        '--test-root', $testRoot,
        '--test-registry-root', $registryRoot)
    if ($IncludeInstallParent -and $requireCustomInstallParent) {
        $arguments += @('--install-parent', $installParent)
    }

    return $arguments
}

function Get-WindowSmokeArguments {
    param(
        [Parameter(Mandatory)]
        [string]$Scenario,
        [Parameter(Mandatory)]
        [string]$CapturePath,
        [Parameter(Mandatory)]
        [string]$MetricsPath,
        [string[]]$Suffix = @()
    )

    $arguments = @(
        '--window-smoke-test', $Scenario,
        '--window-smoke-capture', $CapturePath)
    if ($requireCustomInstallParent) {
        $arguments += @('--window-smoke-metrics', $MetricsPath)
    }

    return $arguments + @($Suffix)
}

function Get-PngDimensions {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $capture = Assert-FoxMouseFile `
        -LiteralPath $LiteralPath `
        -ExpectedParent $captureRoot `
        -Label $Label
    $bytes = [IO.File]::ReadAllBytes($capture)
    if ($bytes.Length -lt 24 -or
        $bytes[0] -ne 0x89 -or
        $bytes[1] -ne 0x50 -or
        $bytes[2] -ne 0x4E -or
        $bytes[3] -ne 0x47 -or
        $bytes[4] -ne 0x0D -or
        $bytes[5] -ne 0x0A -or
        $bytes[6] -ne 0x1A -or
        $bytes[7] -ne 0x0A) {
        throw "$Label is not a valid PNG file."
    }

    $width = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
    $height = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
    if ($width -lt 480 -or $height -lt 280) {
        throw "$Label is unexpectedly small ($width x $height)."
    }

    return [pscustomobject]@{
        Path = $capture
        Width = $width
        Height = $height
        Bytes = $bytes.Length
    }
}

function Assert-WindowCapture {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $dimensions = Get-PngDimensions -LiteralPath $LiteralPath -Label $Label
    Write-Output "$Label verified: $($dimensions.Path) ($($dimensions.Width) x $($dimensions.Height), $($dimensions.Bytes) bytes)."
}

function Assert-ClientWindowPixels {
    param(
        [Parameter(Mandatory)]
        [string]$CapturePath,
        [Parameter(Mandatory)]
        [object]$ClientCaptureBounds,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $dimensions = Get-PngDimensions -LiteralPath $CapturePath -Label "$Label capture"
    $left = [int]$ClientCaptureBounds.x
    $top = [int]$ClientCaptureBounds.y
    $width = [int]$ClientCaptureBounds.width
    $height = [int]$ClientCaptureBounds.height
    if ($left -lt 0 -or $top -lt 0 -or $width -lt 1 -or $height -lt 1 -or
        ($left + $width) -gt $dimensions.Width -or
        ($top + $height) -gt $dimensions.Height) {
        throw "$Label client pixel bounds fall outside the screenshot."
    }

    # Sample only the client area. Quantizing to 5 bits per channel keeps the
    # check stable across font smoothing and GPU color-management differences.
    # Requiring content in multiple tiles rejects a solid/blank client even
    # when the non-client title bar and frame were rendered correctly.
    $bitmap = [Drawing.Bitmap]::new($dimensions.Path)
    try {
        $colorCounts = @{}
        $minimumLuminance = 255
        $maximumLuminance = 0
        $sampleCount = 0
        $tileColumns = 4
        $tileRows = 3
        $tileCount = $tileColumns * $tileRows
        $tileMinimums = New-Object 'int[]' $tileCount
        $tileMaximums = New-Object 'int[]' $tileCount
        for ($index = 0; $index -lt $tileCount; $index++) {
            $tileMinimums[$index] = 255
        }

        $stepX = [Math]::Max(1, [int][Math]::Floor($width / 128.0))
        $stepY = [Math]::Max(1, [int][Math]::Floor($height / 96.0))
        for ($relativeY = 0; $relativeY -lt $height; $relativeY += $stepY) {
            for ($relativeX = 0; $relativeX -lt $width; $relativeX += $stepX) {
                $pixel = $bitmap.GetPixel($left + $relativeX, $top + $relativeY)
                $quantized = (([int]$pixel.R -shr 3) -shl 10) -bor
                    (([int]$pixel.G -shr 3) -shl 5) -bor
                    ([int]$pixel.B -shr 3)
                if ($colorCounts.ContainsKey($quantized)) {
                    $colorCounts[$quantized] = [int]$colorCounts[$quantized] + 1
                }
                else {
                    $colorCounts[$quantized] = 1
                }

                $luminance = [int][Math]::Round(
                    (0.2126 * $pixel.R) + (0.7152 * $pixel.G) + (0.0722 * $pixel.B))
                $minimumLuminance = [Math]::Min($minimumLuminance, $luminance)
                $maximumLuminance = [Math]::Max($maximumLuminance, $luminance)

                $tileX = [Math]::Min(
                    $tileColumns - 1,
                    [int][Math]::Floor(($relativeX * $tileColumns) / [double]$width))
                $tileY = [Math]::Min(
                    $tileRows - 1,
                    [int][Math]::Floor(($relativeY * $tileRows) / [double]$height))
                $tileIndex = ($tileY * $tileColumns) + $tileX
                $tileMinimums[$tileIndex] = [Math]::Min($tileMinimums[$tileIndex], $luminance)
                $tileMaximums[$tileIndex] = [Math]::Max($tileMaximums[$tileIndex], $luminance)
                $sampleCount++
            }
        }

        $dominantColorSamples = 0
        foreach ($count in $colorCounts.Values) {
            $dominantColorSamples = [Math]::Max($dominantColorSamples, [int]$count)
        }
        $nonDominantRatio = if ($sampleCount -gt 0) {
            ($sampleCount - $dominantColorSamples) / [double]$sampleCount
        }
        else {
            0.0
        }
        $variedTiles = 0
        for ($index = 0; $index -lt $tileCount; $index++) {
            if (($tileMaximums[$index] - $tileMinimums[$index]) -ge 16) {
                $variedTiles++
            }
        }

        $luminanceRange = $maximumLuminance - $minimumLuminance
        if ($colorCounts.Count -lt 2 -or
            $luminanceRange -lt 24 -or
            $nonDominantRatio -lt 0.005 -or
            $variedTiles -lt 2) {
            throw ("$Label client area is blank or lacks distributed rendered content " +
                "(colors=$($colorCounts.Count), luminanceRange=$luminanceRange, " +
                "nonDominantRatio=$([Math]::Round($nonDominantRatio, 4)), variedTiles=$variedTiles).")
        }

        Write-Output ("$Label client pixels verified: colors=$($colorCounts.Count), " +
            "luminanceRange=$luminanceRange, " +
            "nonDominantRatio=$([Math]::Round($nonDominantRatio, 4)), variedTiles=$variedTiles/$tileCount.")
    }
    finally {
        $bitmap.Dispose()
    }
}

function Assert-SyntheticBlankClientRejected {
    param(
        [Parameter(Mandatory)]
        [string]$CapturePath,
        [Parameter(Mandatory)]
        [object]$ClientCaptureBounds,
        [Parameter(Mandatory)]
        [string]$Label
    )

    $negativeControlPath = Assert-FoxMouseChildPath `
        -LiteralPath (Join-Path $captureRoot ("synthetic-blank-client-" + [Guid]::NewGuid().ToString('N') + '.png')) `
        -ExpectedParent $captureRoot `
        -Label "$Label synthetic blank negative control"
    $source = Get-PngDimensions -LiteralPath $CapturePath -Label "$Label source capture"
    $bitmap = [Drawing.Bitmap]::new($source.Path)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.FillRectangle(
                [Drawing.Brushes]::White,
                [int]$ClientCaptureBounds.x,
                [int]$ClientCaptureBounds.y,
                [int]$ClientCaptureBounds.width,
                [int]$ClientCaptureBounds.height)
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($negativeControlPath, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    $rejected = $false
    try {
        Assert-ClientWindowPixels `
            -CapturePath $negativeControlPath `
            -ClientCaptureBounds $ClientCaptureBounds `
            -Label "$Label synthetic blank negative control" |
            Out-Null
    }
    catch {
        if ($_.Exception.Message -like '*client area is blank or lacks distributed rendered content*') {
            $rejected = $true
        }
        else {
            throw
        }
    }
    finally {
        if (Test-Path -LiteralPath $negativeControlPath) {
            Remove-Item -LiteralPath $negativeControlPath -Force
        }
    }

    if (-not $rejected) {
        throw "$Label pixel gate accepted a screenshot whose client area was replaced with a solid color."
    }
    Write-Output "$Label synthetic blank-client negative control was rejected."
}

function Assert-WindowMetrics {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [Parameter(Mandatory)]
        [string]$CapturePath,
        [Parameter(Mandatory)]
        [string]$ExpectedScenario,
        [Parameter(Mandatory)]
        [string]$ExpectedUiState,
        [Parameter(Mandatory)]
        [string[]]$ExpectedFooterButtons,
        [string[]]$ExpectedVisibleControls = @(),
        [string[]]$ExpectedHiddenControls = @(),
        [Parameter(Mandatory)]
        [string]$Label
    )

    $metricsPath = Assert-FoxMouseFile `
        -LiteralPath $LiteralPath `
        -ExpectedParent $captureRoot `
        -Label "$Label metrics"
    $metrics = Get-Content -LiteralPath $metricsPath -Raw -ErrorAction Stop | ConvertFrom-Json
    if ($metrics.schema -ne 'foxmouse.maintenance-window/1' -or
        -not [string]::Equals([string]$metrics.scenario, $ExpectedScenario, [StringComparison]::Ordinal) -or
        -not [string]::Equals([string]$metrics.uiState, $ExpectedUiState, [StringComparison]::Ordinal) -or
        [int]$metrics.dpi -lt 96 -or
        [bool]$metrics.autoScroll -or
        [bool]$metrics.horizontalScrollVisible -or
        [bool]$metrics.verticalScrollVisible -or
        [bool]$metrics.contentHorizontalScrollVisible -or
        [bool]$metrics.contentVerticalScrollVisible -or
        -not [bool]$metrics.visibleControlsInsideClient -or
        [int]$metrics.overlapCount -ne 0 -or
        @($metrics.overlaps).Count -ne 0 -or
        @($metrics.clippedControls).Count -ne 0 -or
        -not [bool]$metrics.footerSingleRow -or
        -not [string]::Equals([string]$metrics.rootDock, 'Fill', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label reported an invalid, clipped, overlapping, wrapped, or scrollable window state."
    }

    foreach ($boundsName in @('clientBounds', 'captureBounds', 'clientCaptureBounds', 'rootBounds')) {
        $bounds = $metrics.$boundsName
        if ($null -eq $bounds -or [int]$bounds.width -lt 1 -or [int]$bounds.height -lt 1) {
            throw "$Label has invalid $boundsName metrics."
        }
    }
    if ([int]$metrics.rootBounds.x -ne 0 -or
        [int]$metrics.rootBounds.y -ne 0 -or
        [int]$metrics.rootBounds.width -ne [int]$metrics.clientBounds.width -or
        [int]$metrics.rootBounds.height -ne [int]$metrics.clientBounds.height) {
        throw "$Label root layout does not fill the client area."
    }
    if ([int]$metrics.captureBounds.width -lt [int]$metrics.clientBounds.width -or
        [int]$metrics.captureBounds.height -le [int]$metrics.clientBounds.height) {
        throw "$Label capture does not include the complete framed window."
    }
    if ([int]$metrics.clientCaptureBounds.x -lt 0 -or
        [int]$metrics.clientCaptureBounds.y -lt 0 -or
        [int]$metrics.clientCaptureBounds.width -ne [int]$metrics.clientBounds.width -or
        [int]$metrics.clientCaptureBounds.height -ne [int]$metrics.clientBounds.height -or
        ([int]$metrics.clientCaptureBounds.x + [int]$metrics.clientCaptureBounds.width) -gt [int]$metrics.captureBounds.width -or
        ([int]$metrics.clientCaptureBounds.y + [int]$metrics.clientCaptureBounds.height) -gt [int]$metrics.captureBounds.height) {
        throw "$Label client capture bounds are invalid or incomplete."
    }

    $capture = Get-PngDimensions -LiteralPath $CapturePath -Label "$Label capture"
    if ($capture.Width -ne [int]$metrics.captureBounds.width -or
        $capture.Height -ne [int]$metrics.captureBounds.height) {
        throw "$Label screenshot dimensions disagree with its metrics."
    }
    Assert-ClientWindowPixels `
        -CapturePath $capture.Path `
        -ClientCaptureBounds $metrics.clientCaptureBounds `
        -Label $Label
    Assert-SyntheticBlankClientRejected `
        -CapturePath $capture.Path `
        -ClientCaptureBounds $metrics.clientCaptureBounds `
        -Label $Label

    $actualFooterButtons = @($metrics.visibleFooterButtons | ForEach-Object { [string]$_ } | Sort-Object)
    $expectedSortedButtons = @($ExpectedFooterButtons | Sort-Object)
    if (($actualFooterButtons -join [char]0) -ne ($expectedSortedButtons -join [char]0)) {
        throw "$Label footer buttons '$($actualFooterButtons -join ', ')' do not match '$($expectedSortedButtons -join ', ')'."
    }

    $visibleControlNames = @($metrics.visibleControls | ForEach-Object { [string]$_.name })
    foreach ($controlName in $ExpectedVisibleControls) {
        if ($controlName -notin $visibleControlNames) {
            throw "$Label did not report required visible control '$controlName'."
        }
    }
    foreach ($controlName in $ExpectedHiddenControls) {
        if ($controlName -in $visibleControlNames) {
            throw "$Label unexpectedly reported hidden control '$controlName' as visible."
        }
    }

    Write-Output "$Label metrics verified: $metricsPath ($($metrics.clientBounds.width) x $($metrics.clientBounds.height) client, $($metrics.dpi) DPI)."
}

function Remove-CompletedCleanupWorker {
    param(
        [Parameter(Mandatory)]
        [object]$CleanupResult,
        [Parameter(Mandatory)]
        [string]$StatusPath,
        [Parameter(Mandatory)]
        [string]$Label
    )

    try {
        $worker = [Diagnostics.Process]::GetProcessById([int]$CleanupResult.workerProcessId)
        try {
            if (-not $worker.WaitForExit(10000)) {
                throw "$Label worker did not exit after reporting completion."
            }
        }
        finally {
            $worker.Dispose()
        }
    }
    catch [ArgumentException] {
        # The worker exited before its result was consumed.
    }

    $workerRoot = Assert-FoxMouseDirectoryRoot `
        -LiteralPath ([string]$CleanupResult.workerRoot) `
        -ExpectedParent $tempRoot `
        -Label "$Label worker root"
    if ((Split-Path -Leaf $workerRoot) -notmatch '^FoxMouse-CleanupWorker-[0-9a-fA-F]{32}$') {
        throw "$Label worker used an unexpected temporary directory."
    }
    Remove-FoxMouseTree `
        -LiteralPath $workerRoot `
        -ExpectedParent $tempRoot `
        -Label "$Label worker cleanup"
    Remove-Item -LiteralPath $StatusPath -Force
}

function Assert-InstalledState {
    $app = Assert-FoxMouseFile -LiteralPath (Join-Path $installRoot 'FoxMouse.exe') -ExpectedParent $installRoot -Label 'installed app'
    $null = Assert-FoxMouseFile -LiteralPath (Join-Path $installRoot 'FoxMouse.Uninstall.exe') -ExpectedParent $installRoot -Label 'root uninstaller'
    $null = Assert-FoxMouseFile -LiteralPath (Join-Path $installRoot 'FoxMouse.Cleanup.exe') -ExpectedParent $installRoot -Label 'root cleanup worker'
    $null = Assert-FoxMouseFile -LiteralPath $maintenanceHost -ExpectedParent $settingsRoot -Label 'synchronous maintenance host'
    $null = Assert-FoxMouseFile -LiteralPath (Join-Path (Split-Path -Parent $maintenanceHost) 'FoxMouse.Cleanup.exe') -ExpectedParent $settingsRoot -Label 'maintenance cleanup worker'
    $null = Assert-FoxMouseFile -LiteralPath $shortcutPath -ExpectedParent $testRoot -Label 'Start menu shortcut'
    if (-not (Test-Path -LiteralPath $arpPath)) {
        throw 'The isolated HKCU Installed Apps registration is missing.'
    }

    $arp = Get-ItemProperty -LiteralPath $arpPath
    if ($arp.DisplayName -ne 'FoxMouse' -or
        -not [string]::Equals([string]$arp.InstallLocation, $installRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $arp.DisplayIcon -ne ('"' + $app + '",0') -or
        $arp.UninstallString -ne ('"' + (Join-Path $installRoot 'FoxMouse.Uninstall.exe') + '"') -or
        $arp.ModifyPath -ne ('"' + (Join-Path $installRoot 'FoxMouse.Uninstall.exe') + '"') -or
        $arp.QuietUninstallString -ne ('"' + $maintenanceHost + '" --uninstall --quiet')) {
        throw 'The isolated HKCU Installed Apps maintenance contract is invalid.'
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and
        -not [string]::Equals([string]$arp.DisplayVersion, $ExpectedVersion, [StringComparison]::Ordinal)) {
        throw "Installed Apps version '$($arp.DisplayVersion)' does not match expected version '$ExpectedVersion'."
    }
    if ($requireCustomInstallParent) {
        $installStatePath = Assert-FoxMouseFile `
            -LiteralPath (Join-Path $installRoot '.foxmouse-install.json') `
            -ExpectedParent $installRoot `
            -Label 'location-bound install state'
        $locationState = Get-Content -LiteralPath $installStatePath -Raw | ConvertFrom-Json
        if ([int]$locationState.SchemaVersion -ne 2 -or
            $locationState.ProductId -ne 'FoxMouse.Desktop' -or
            [string]$locationState.InstallId -notmatch '^[0-9a-fA-F]{32}$' -or
            -not [string]::Equals([string]$locationState.InstallRoot, $installRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals((Split-Path -Parent $installRoot), $installParent, [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals((Split-Path -Leaf $installRoot), 'FoxMouse', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The installed state is not bound to exactly one canonical custom FoxMouse root.'
        }
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        if (-not [string]::Equals($shortcut.TargetPath, $app, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The real Start menu shortcut target is invalid.'
        }
        if (-not [string]::Equals($shortcut.WorkingDirectory, $installRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The real Start menu shortcut working directory is invalid.'
        }
    }
    finally {
        if ($null -ne $shortcut) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        }
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }

    if ($requireCustomInstallParent -and (Test-Path -LiteralPath $defaultInstallRoot)) {
        throw "The custom-location lifecycle wrote to the default install root: $defaultInstallRoot"
    }
    if ($requireCustomInstallParent -and -not (Test-Path -LiteralPath $installParentSentinel -PathType Leaf)) {
        throw 'The deployment lifecycle removed a sibling file from the selected installation parent.'
    }
}

$previousTestOptIn = [Environment]::GetEnvironmentVariable('FOXMOUSE_ENABLE_ISOLATED_DEPLOYMENT_TESTS', 'Process')
[Environment]::SetEnvironmentVariable('FOXMOUSE_ENABLE_ISOLATED_DEPLOYMENT_TESTS', '1', 'Process')
try {
    [void][IO.Directory]::CreateDirectory($testRoot)
    [void][IO.Directory]::CreateDirectory($installParent)
    Set-Content -LiteralPath $installParentSentinel -Value 'FoxMouse lifecycle sibling sentinel' -Encoding utf8 -NoNewline

    # The real graphical entry point must honor its Close action even when no
    # operation has started. Capture the complete framed window and its layout
    # metrics as durable M5 evidence when Accept-Milestone supplies a root.
    Invoke-LifecycleExecutable `
        -Executable $setup `
        -Arguments (Get-LifecycleArguments `
            -IncludeInstallParent `
            -Prefix (Get-WindowSmokeArguments `
                -Scenario 'idle' `
                -CapturePath $setupWindowCapture `
                -MetricsPath $setupWindowMetrics)) `
        -Label 'graphical Setup idle close'
    Assert-WindowCapture -LiteralPath $setupWindowCapture -Label 'graphical Setup capture'
    if ($requireCustomInstallParent) {
        Assert-WindowMetrics `
            -LiteralPath $setupWindowMetrics `
            -CapturePath $setupWindowCapture `
            -ExpectedScenario 'idle' `
            -ExpectedUiState 'Idle' `
            -ExpectedFooterButtons @('PrimaryAction', 'CloseAction') `
            -ExpectedVisibleControls @('LocationPanel', 'ChangeLocation', 'PrimaryAction', 'CloseAction') `
            -ExpectedHiddenControls @('FeedbackPanel', 'KeepSettings', 'RepairAction', 'UninstallAction') `
            -Label 'graphical Setup idle'
    }

    Invoke-LifecycleExecutable `
        -Executable $setup `
        -Arguments (Get-LifecycleArguments -IncludeInstallParent -Prefix @(
            '--window-smoke-test', 'install',
            '--no-launch')) `
        -Label 'graphical clean install auto-close'
    Assert-InstalledState

    $repairProbe = Join-Path $installRoot 'README.md'
    Remove-Item -LiteralPath $repairProbe -Force -ErrorAction Stop
    Invoke-LifecycleExecutable `
        -Executable $setup `
        -Arguments (Get-LifecycleArguments -Prefix @(
            '--window-smoke-test', 'repair',
            '--no-launch')) `
        -Label 'graphical same-version repair auto-close'
    $null = Assert-FoxMouseFile -LiteralPath $repairProbe -ExpectedParent $installRoot -Label 'repair-restored payload'

    # A stale-looking worker root with a directory masquerading as the helper
    # must never be recursively removed by stale-worker housekeeping.
    [void][IO.Directory]::CreateDirectory((Join-Path $maliciousWorkerRoot 'FoxMouse.Cleanup.exe'))
    (Get-Item -LiteralPath $maliciousWorkerRoot).LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(-2)

    # Damage a payload file again and repair from the installed root entry.
    # This executes the detached handoff and consumes the state-bound cache,
    # rather than using Setup's embedded package.
    $repairProbeHashBefore = (Get-FileHash -LiteralPath $repairProbe -Algorithm SHA256).Hash
    Remove-Item -LiteralPath $repairProbe -Force -ErrorAction Stop
    $rootUninstaller = Join-Path $installRoot 'FoxMouse.Uninstall.exe'
    $rootRepairStopwatch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-LifecycleExecutable `
        -Executable $rootUninstaller `
        -Arguments (Get-LifecycleArguments `
            -Prefix (Get-WindowSmokeArguments `
                -Scenario 'repair' `
                -CapturePath $rootWindowCapture `
                -MetricsPath $rootWindowMetrics `
                -Suffix @('--no-launch', '--result-file', $repairResultPath))) `
        -Label 'root graphical cached repair handoff'
    $repairDeadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not (Test-Path -LiteralPath $repairResultPath) -and [DateTime]::UtcNow -lt $repairDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $repairResultPath)) {
        throw 'Detached root repair did not publish a completion result.'
    }

    $rootRepairResult = Get-Content -LiteralPath $repairResultPath -Raw | ConvertFrom-Json
    if ($rootRepairResult.schema -ne 'foxmouse.maintenance-result/1' -or $rootRepairResult.exitCode -ne 0) {
        throw "Detached root cached repair reported failure (exit=$($rootRepairResult.exitCode), cleanup=$($rootRepairResult.cleanupStartError))."
    }
    Assert-WindowCapture -LiteralPath $rootWindowCapture -Label 'root graphical maintenance capture'
    if ($requireCustomInstallParent) {
        Assert-WindowMetrics `
            -LiteralPath $rootWindowMetrics `
            -CapturePath $rootWindowCapture `
            -ExpectedScenario 'repair' `
            -ExpectedUiState 'Idle' `
            -ExpectedFooterButtons @('RepairAction', 'UninstallAction', 'CloseAction') `
            -ExpectedVisibleControls @('LocationPanel', 'RepairAction', 'UninstallAction', 'CloseAction') `
            -ExpectedHiddenControls @('ChangeLocation', 'FeedbackPanel', 'KeepSettings', 'PrimaryAction') `
            -Label 'root installed maintenance'
    }
    $repairCleanupStatus = [string]$rootRepairResult.cleanupStatusFile
    $repairCleanupDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $repairCleanupStatus) -and [DateTime]::UtcNow -lt $repairCleanupDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $repairCleanupStatus)) {
        throw 'Detached root repair cleanup did not publish a completion result.'
    }
    $repairCleanup = Get-Content -LiteralPath $repairCleanupStatus -Raw | ConvertFrom-Json
    if ($repairCleanup.schema -ne 'foxmouse.cleanup-result/1' -or -not [bool]$repairCleanup.success) {
        throw "Detached root repair cleanup failed: $($repairCleanup.error)"
    }
    $rootRepairStopwatch.Stop()
    Write-Output "Root graphical cached repair completed after $($rootRepairStopwatch.ElapsedMilliseconds) ms."
    if (-not (Test-Path -LiteralPath $repairProbe) -or
        (Test-Path -LiteralPath ([string]$rootRepairResult.temporaryHostRoot))) {
        throw 'Root cached repair did not restore the payload or clean its detached host.'
    }
    if ((Get-FileHash -LiteralPath $repairProbe -Algorithm SHA256).Hash -ne $repairProbeHashBefore) {
        throw 'Root cached repair restored a payload with the wrong SHA-256 hash.'
    }
    if (-not (Test-Path -LiteralPath (Join-Path $maliciousWorkerRoot 'FoxMouse.Cleanup.exe') -PathType Container)) {
        throw 'Stale-worker cleanup recursively removed an unexpected directory tree.'
    }

    $cachedPackage = Join-Path $settingsRoot 'InstallerCache\FoxMouse-package.zip'
    $cachedHash = (Get-FileHash -LiteralPath $cachedPackage -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashFileValue = (Get-Content -LiteralPath (Join-Path $settingsRoot 'InstallerCache\FoxMouse-package.sha256') -Raw).Trim().ToLowerInvariant()
    $installState = Get-Content -LiteralPath (Join-Path $installRoot '.foxmouse-install.json') -Raw | ConvertFrom-Json
    if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and
        -not [string]::Equals([string]$installState.Version, $ExpectedVersion, [StringComparison]::Ordinal)) {
        throw "Installed state version '$($installState.Version)' does not match expected version '$ExpectedVersion'."
    }
    if ($cachedHash -ne $hashFileValue -or $cachedHash -ne ([string]$installState.PackageSha256).ToLowerInvariant()) {
        throw 'Root repair cache hash is not bound to the active install state.'
    }
    Remove-CompletedCleanupWorker `
        -CleanupResult $repairCleanup `
        -StatusPath $repairCleanupStatus `
        -Label 'root repair cleanup'
    Remove-Item -LiteralPath $repairResultPath -Force

    if ($requireCustomInstallParent) {
        # Enter the confirmation state without executing uninstall. It replaces
        # the maintenance content in-place and must remain fully visible without
        # any scroll container or duplicate footer actions.
        Invoke-LifecycleExecutable `
            -Executable $maintenanceHost `
            -Arguments (Get-LifecycleArguments `
                -Prefix (Get-WindowSmokeArguments `
                    -Scenario 'uninstall-confirmation' `
                    -CapturePath $confirmationWindowCapture `
                    -MetricsPath $confirmationWindowMetrics)) `
            -Label 'graphical uninstall confirmation close'
        Assert-WindowCapture `
            -LiteralPath $confirmationWindowCapture `
            -Label 'graphical uninstall confirmation capture'
        Assert-WindowMetrics `
            -LiteralPath $confirmationWindowMetrics `
            -CapturePath $confirmationWindowCapture `
            -ExpectedScenario 'uninstall-confirmation' `
            -ExpectedUiState 'ConfirmingUninstall' `
            -ExpectedFooterButtons @('ConfirmUninstall', 'CancelUninstall') `
            -ExpectedVisibleControls @('LocationPanel', 'FeedbackPanel', 'KeepSettings', 'ConfirmUninstall', 'CancelUninstall') `
            -ExpectedHiddenControls @('ChangeLocation', 'PrimaryAction', 'RepairAction', 'UninstallAction', 'CloseAction') `
            -Label 'graphical uninstall confirmation'
        Assert-InstalledState
    }

    # Exercise the root entry point's parent/child handoff. The parent only
    # returns after the detached worker has acknowledged startup; the result
    # file is written by that worker after the real uninstall transaction.
    $rootUninstaller = Join-Path $installRoot 'FoxMouse.Uninstall.exe'
    $rootUninstallStopwatch = [Diagnostics.Stopwatch]::StartNew()
    Invoke-LifecycleExecutable `
        -Executable $rootUninstaller `
        -Arguments (Get-LifecycleArguments -Prefix @(
            '--window-smoke-test', 'uninstall',
            '--result-file', $resultPath)) `
        -Label 'root graphical uninstall handoff'
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not (Test-Path -LiteralPath $resultPath) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $resultPath)) {
        throw 'Detached root uninstaller did not publish a completion result.'
    }

    $detachedResult = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    if ($detachedResult.schema -ne 'foxmouse.maintenance-result/1' -or $detachedResult.exitCode -ne 0) {
        throw 'Detached root uninstaller reported failure.'
    }
    $cleanupStatusPath = [string]$detachedResult.cleanupStatusFile
    $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $cleanupStatusPath) -and [DateTime]::UtcNow -lt $cleanupDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $cleanupStatusPath)) {
        throw 'Detached cleanup worker did not publish a completion result.'
    }

    $cleanupResult = Get-Content -LiteralPath $cleanupStatusPath -Raw | ConvertFrom-Json
    if ($cleanupResult.schema -ne 'foxmouse.cleanup-result/1' -or -not [bool]$cleanupResult.success) {
        throw "Detached cleanup worker failed: $($cleanupResult.error)"
    }
    $rootUninstallStopwatch.Stop()
    Write-Output "Root graphical uninstall completed after $($rootUninstallStopwatch.ElapsedMilliseconds) ms."

    $temporaryHostRoot = Assert-FoxMouseChildPath `
        -LiteralPath ([string]$detachedResult.temporaryHostRoot) `
        -ExpectedParent $tempRoot `
        -Label 'detached lifecycle host root'
    if (-not (Split-Path -Leaf $temporaryHostRoot).StartsWith('FoxMouse-Uninstall-Host-', [StringComparison]::Ordinal)) {
        throw 'Detached lifecycle host used an unexpected temporary directory.'
    }
    if (Test-Path -LiteralPath $temporaryHostRoot) {
        throw 'Detached cleanup worker left the temporary maintenance host behind.'
    }
    Remove-CompletedCleanupWorker `
        -CleanupResult $cleanupResult `
        -StatusPath $cleanupStatusPath `
        -Label 'root uninstall cleanup'

    if ((Test-Path -LiteralPath $installRoot) -or
        (Test-Path -LiteralPath $arpPath) -or
        (Test-Path -LiteralPath $shortcutPath)) {
        throw 'Detached uninstall left installed application state behind.'
    }
    if ($requireCustomInstallParent -and ((Test-Path -LiteralPath $defaultInstallRoot) -or
        -not (Test-Path -LiteralPath $installParentSentinel -PathType Leaf))) {
        throw 'Detached uninstall crossed the selected FoxMouse leaf boundary.'
    }

    Remove-Item -LiteralPath $resultPath -Force
    Invoke-LifecycleExecutable `
        -Executable $setup `
        -Arguments (Get-LifecycleArguments -IncludeInstallParent -Prefix @('--install', '--quiet', '--no-launch')) `
        -Label 'isolated reinstall before quiet ARP test'
    Assert-InstalledState

    # A missing Guard forces maintenance to fail before mutation. The external
    # ARP maintenance host must remain synchronous and return the real code.
    $guard = Join-Path $installRoot 'FoxMouse.Guard.exe'
    $guardBackup = "$guard.lifecycle-backup"
    Move-Item -LiteralPath $guard -Destination $guardBackup
    Invoke-LifecycleExecutable `
        -Executable $maintenanceHost `
        -Arguments (Get-LifecycleArguments -Prefix @(
            '--window-smoke-test', 'uninstall',
            '--window-smoke-request-close')) `
        -Label 'graphical failure with deferred close' `
        -ExpectedExitCode 1
    if (-not (Test-Path -LiteralPath $arpPath) -or -not (Test-Path -LiteralPath $installRoot)) {
        throw 'Failed graphical maintenance unexpectedly mutated the installed state.'
    }

    # Retain the synchronous quiet-ARP regression in addition to the real
    # window failure/close path above.
    Invoke-LifecycleExecutable `
        -Executable $maintenanceHost `
        -Arguments (Get-LifecycleArguments -Prefix @('--uninstall', '--quiet')) `
        -Label 'quiet ARP failure propagation' `
        -ExpectedExitCode 1
    if (-not (Test-Path -LiteralPath $arpPath) -or -not (Test-Path -LiteralPath $installRoot)) {
        throw 'Failed quiet maintenance unexpectedly mutated the installed state.'
    }

    Move-Item -LiteralPath $guardBackup -Destination $guard
    Invoke-LifecycleExecutable `
        -Executable $maintenanceHost `
        -Arguments (Get-LifecycleArguments -Prefix @('--uninstall', '--quiet', '--result-file', $quietResultPath)) `
        -Label 'quiet ARP uninstall'
    $quietResult = Get-Content -LiteralPath $quietResultPath -Raw | ConvertFrom-Json
    if ($quietResult.schema -ne 'foxmouse.maintenance-result/1' -or $quietResult.exitCode -ne 0) {
        throw 'Quiet ARP maintenance host reported failure.'
    }
    $quietCleanupStatus = [string]$quietResult.cleanupStatusFile
    $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $quietCleanupStatus) -and [DateTime]::UtcNow -lt $cleanupDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $quietCleanupStatus)) {
        throw 'Quiet ARP cleanup worker did not publish a completion result.'
    }
    $quietCleanup = Get-Content -LiteralPath $quietCleanupStatus -Raw | ConvertFrom-Json
    if ($quietCleanup.schema -ne 'foxmouse.cleanup-result/1' -or -not [bool]$quietCleanup.success) {
        throw "Quiet ARP cleanup worker failed: $($quietCleanup.error)"
    }
    if ((Test-Path -LiteralPath $installRoot) -or
        (Test-Path -LiteralPath $arpPath) -or
        (Test-Path -LiteralPath $shortcutPath) -or
        (Test-Path -LiteralPath (Split-Path -Parent $maintenanceHost))) {
        throw 'Quiet ARP uninstall left installed application state behind.'
    }
    if ($requireCustomInstallParent -and ((Test-Path -LiteralPath $defaultInstallRoot) -or
        -not (Test-Path -LiteralPath $installParentSentinel -PathType Leaf))) {
        throw 'Quiet ARP uninstall crossed the selected FoxMouse leaf boundary.'
    }
    Remove-CompletedCleanupWorker `
        -CleanupResult $quietCleanup `
        -StatusPath $quietCleanupStatus `
        -Label 'quiet ARP cleanup'
    Remove-Item -LiteralPath $quietResultPath -Force

    Invoke-LifecycleExecutable `
        -Executable $setup `
        -Arguments (Get-LifecycleArguments -IncludeInstallParent -Prefix @('--install', '--quiet', '--no-launch')) `
        -Label 'isolated reinstall before delete-settings test'
    [void][IO.Directory]::CreateDirectory($settingsRoot)
    Set-Content -LiteralPath (Join-Path $settingsRoot 'delete-settings.marker') -Value 'delete' -Encoding utf8
    $rootUninstaller = Join-Path $installRoot 'FoxMouse.Uninstall.exe'
    Invoke-LifecycleExecutable `
        -Executable $rootUninstaller `
        -Arguments (Get-LifecycleArguments -Prefix @('--uninstall', '--quiet', '--delete-settings', '--result-file', $deleteResultPath)) `
        -Label 'detached delete-settings uninstall'
    $deleteDeadline = [DateTime]::UtcNow.AddSeconds(60)
    while (-not (Test-Path -LiteralPath $deleteResultPath) -and [DateTime]::UtcNow -lt $deleteDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $deleteResultPath)) {
        throw 'Delete-settings uninstall did not publish a completion result.'
    }
    $deleteResult = Get-Content -LiteralPath $deleteResultPath -Raw | ConvertFrom-Json
    if ($deleteResult.schema -ne 'foxmouse.maintenance-result/1' -or $deleteResult.exitCode -ne 0) {
        throw "Delete-settings uninstall reported failure (exit=$($deleteResult.exitCode), operation=$($deleteResult.operationError), cleanup=$($deleteResult.cleanupStartError))."
    }
    $deleteCleanupStatus = [string]$deleteResult.cleanupStatusFile
    $deleteCleanupDeadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $deleteCleanupStatus) -and [DateTime]::UtcNow -lt $deleteCleanupDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if (-not (Test-Path -LiteralPath $deleteCleanupStatus)) {
        throw 'Delete-settings cleanup did not publish a completion result.'
    }
    $deleteCleanup = Get-Content -LiteralPath $deleteCleanupStatus -Raw | ConvertFrom-Json
    if ($deleteCleanup.schema -ne 'foxmouse.cleanup-result/1' -or -not [bool]$deleteCleanup.success) {
        throw "Delete-settings cleanup failed: $($deleteCleanup.error)"
    }
    if ((Test-Path -LiteralPath $installRoot) -or
        (Test-Path -LiteralPath $settingsRoot) -or
        (Test-Path -LiteralPath $arpPath) -or
        (Test-Path -LiteralPath $shortcutPath)) {
        throw 'Delete-settings uninstall recreated or retained product state.'
    }
    if ($requireCustomInstallParent -and ((Test-Path -LiteralPath $defaultInstallRoot) -or
        -not (Test-Path -LiteralPath $installParentSentinel -PathType Leaf))) {
        throw 'Delete-settings uninstall crossed the selected FoxMouse leaf boundary.'
    }
    Remove-CompletedCleanupWorker `
        -CleanupResult $deleteCleanup `
        -StatusPath $deleteCleanupStatus `
        -Label 'delete-settings cleanup'
    Remove-Item -LiteralPath $deleteResultPath -Force

    Write-Output "FoxMouse isolated installer lifecycle verified: $testRoot"
}
finally {
    [Environment]::SetEnvironmentVariable(
        'FOXMOUSE_ENABLE_ISOLATED_DEPLOYMENT_TESTS',
        $previousTestOptIn,
        'Process')
    if (Test-Path -LiteralPath $arpPath) {
        Remove-Item -LiteralPath $arpPath -Recurse -Force -ErrorAction SilentlyContinue
    }
    $registryTestRoot = "HKCU:\$registryRoot"
    if (Test-Path -LiteralPath $registryTestRoot) {
        Remove-Item -LiteralPath $registryTestRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $resultPath) {
        Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $repairResultPath) {
        Remove-Item -LiteralPath $repairResultPath -Force -ErrorAction SilentlyContinue
    }
    $repairCleanupStatusPath = "$repairResultPath.cleanup.json"
    if (Test-Path -LiteralPath $repairCleanupStatusPath) {
        Remove-Item -LiteralPath $repairCleanupStatusPath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $quietResultPath) {
        Remove-Item -LiteralPath $quietResultPath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $deleteResultPath) {
        Remove-Item -LiteralPath $deleteResultPath -Force -ErrorAction SilentlyContinue
    }
    $deleteCleanupStatusPath = "$deleteResultPath.cleanup.json"
    if (Test-Path -LiteralPath $deleteCleanupStatusPath) {
        Remove-Item -LiteralPath $deleteCleanupStatusPath -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $testRoot) {
        $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $testRoot -Label 'installer lifecycle cleanup root'
        Remove-FoxMouseTree `
            -LiteralPath $testRoot `
            -ExpectedParent $tempRoot `
            -Label 'installer lifecycle cleanup root'
    }
    if (Test-Path -LiteralPath $maliciousWorkerRoot) {
        $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $maliciousWorkerRoot -Label 'malicious worker fixture cleanup root'
        Remove-FoxMouseTree `
            -LiteralPath $maliciousWorkerRoot `
            -ExpectedParent $tempRoot `
            -Label 'malicious worker fixture cleanup root'
    }
}
