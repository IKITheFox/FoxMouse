[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,
    [switch]$NoLaunch,
    [Alias('CertificateThumbprint')]
    [string]$ExpectedSignerThumbprint
)

$ErrorActionPreference = 'Stop'
$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptRoot 'FoxMouse.ScriptSafety.ps1')

$localAppData = Get-FoxMouseFullPath -LiteralPath ([Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData))
$null = Assert-FoxMouseDirectoryRoot -LiteralPath $localAppData -Label 'LocalAppData root'

$programsRoot = Get-FoxMouseFullPath -LiteralPath (Join-Path $localAppData 'Programs')
$null = Assert-FoxMouseChildPath -LiteralPath $programsRoot -ExpectedParent $localAppData -Label 'Programs root'
if (-not (Test-Path -LiteralPath $programsRoot)) {
    [void][IO.Directory]::CreateDirectory($programsRoot)
}
$null = Assert-FoxMouseDirectoryRoot `
    -LiteralPath $programsRoot `
    -ExpectedParent $localAppData `
    -Label 'Programs root'

$installRoot = Get-FoxMouseFullPath -LiteralPath (Join-Path $programsRoot 'FoxMouse')
$null = Assert-FoxMouseChildPath -LiteralPath $installRoot -ExpectedParent $programsRoot -Label 'install root'
if (Test-Path -LiteralPath $installRoot) {
    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $installRoot -Label 'install root'
}

$startMenuRoot = Get-FoxMouseFullPath -LiteralPath ([Environment]::GetFolderPath(
    [Environment+SpecialFolder]::Programs))
$null = Assert-FoxMouseDirectoryRoot -LiteralPath $startMenuRoot -Label 'Start Menu Programs root'
$shortcutPath = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $startMenuRoot 'FoxMouse.lnk') `
    -ExpectedParent $startMenuRoot `
    -Label 'Start Menu shortcut'

$packageItem = Get-Item -LiteralPath $PackagePath -Force -ErrorAction Stop
if ($packageItem.PSIsContainer -or
    ($packageItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
    -not [string]::Equals($packageItem.Extension, '.zip', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'PackagePath must be a regular, non-reparse ZIP file.'
}
$resolvedPackage = Get-FoxMouseFullPath -LiteralPath $packageItem.FullName

$expectedThumbprint = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
    $expectedThumbprint = Normalize-FoxMouseThumbprint -Thumbprint $ExpectedSignerThumbprint
}

$tempRoot = Get-FoxMouseFullPath -LiteralPath ([IO.Path]::GetTempPath())
$null = Assert-FoxMouseDirectoryRoot -LiteralPath $tempRoot -Label 'temporary root'
$transactionId = [Guid]::NewGuid().ToString('N')
$stagingRoot = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $tempRoot "FoxMouse-Install-$transactionId") `
    -ExpectedParent $tempRoot `
    -Label 'staging root'
$candidateRoot = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $programsRoot ".FoxMouse.staging.$transactionId") `
    -ExpectedParent $programsRoot `
    -Label 'install candidate'
$backupRoot = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $programsRoot ".FoxMouse.backup.$transactionId") `
    -ExpectedParent $programsRoot `
    -Label 'install backup'

[void][IO.Directory]::CreateDirectory($stagingRoot)
$null = Assert-FoxMouseDirectoryRoot `
    -LiteralPath $stagingRoot `
    -ExpectedParent $tempRoot `
    -Label 'staging root'

$committed = $false
$oldInstallBackedUp = $false
$newInstallActivated = $false
$previousShortcutExists = $false
$shortcutBackup = Join-Path $stagingRoot 'FoxMouse.previous.lnk'
$launchedProcess = $null
$payloadGuard = $null
$previousAppWasRunning = $false

try {
    $expandedRoot = Join-Path $stagingRoot 'expanded'
    [void][IO.Directory]::CreateDirectory($expandedRoot)
    Assert-FoxMouseZipEntriesSafe `
        -ArchivePath $resolvedPackage `
        -DestinationRoot $expandedRoot
    Expand-Archive -LiteralPath $resolvedPackage -DestinationPath $expandedRoot -Force
    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $expandedRoot -Label 'expanded package root'

    $topLevelItems = @(Get-ChildItem -LiteralPath $expandedRoot -Force)
    $payloadRoot = Join-Path $expandedRoot 'FoxMouse'
    if ($topLevelItems.Count -ne 1 -or
        -not $topLevelItems[0].PSIsContainer -or
        -not (Test-FoxMousePathEqual -Left $topLevelItems[0].FullName -Right $payloadRoot)) {
        throw 'The package must contain exactly one top-level FoxMouse directory.'
    }

    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $payloadRoot -Label 'FoxMouse payload'
    $payloadApp = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $payloadRoot 'FoxMouse.exe') `
        -ExpectedParent $payloadRoot `
        -Label 'FoxMouse.exe payload'
    $payloadGuard = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $payloadRoot 'FoxMouse.Guard.exe') `
        -ExpectedParent $payloadRoot `
        -Label 'FoxMouse.Guard.exe payload'
    $payloadSettingsRoot = Assert-FoxMouseDirectoryRoot `
        -LiteralPath (Join-Path $payloadRoot 'Settings') `
        -ExpectedParent $payloadRoot `
        -Label 'FoxMouse Settings payload directory'
    $payloadSettings = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $payloadSettingsRoot 'FoxMouse.Settings.exe') `
        -ExpectedParent $payloadSettingsRoot `
        -Label 'FoxMouse.Settings.exe payload'

    if ($null -ne $expectedThumbprint) {
        Assert-FoxMouseAuthenticodeSignature `
            -LiteralPath $payloadApp `
            -ExpectedSignerThumbprint $expectedThumbprint
        Assert-FoxMouseAuthenticodeSignature `
            -LiteralPath $payloadGuard `
            -ExpectedSignerThumbprint $expectedThumbprint
        Assert-FoxMouseAuthenticodeSignature `
            -LiteralPath $payloadSettings `
            -ExpectedSignerThumbprint $expectedThumbprint
    }

    if (Test-Path -LiteralPath $candidateRoot) {
        throw "Install candidate unexpectedly exists: $candidateRoot"
    }
    [void][IO.Directory]::CreateDirectory($candidateRoot)
    foreach ($payloadItem in @(Get-ChildItem -LiteralPath $payloadRoot -Force)) {
        Copy-Item -LiteralPath $payloadItem.FullName -Destination $candidateRoot -Recurse -Force -ErrorAction Stop
    }

    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $candidateRoot -Label 'install candidate'
    $candidateApp = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $candidateRoot 'FoxMouse.exe') `
        -ExpectedParent $candidateRoot `
        -Label 'staged FoxMouse.exe'
    $candidateGuard = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $candidateRoot 'FoxMouse.Guard.exe') `
        -ExpectedParent $candidateRoot `
        -Label 'staged FoxMouse.Guard.exe'
    $candidateSettingsRoot = Assert-FoxMouseDirectoryRoot `
        -LiteralPath (Join-Path $candidateRoot 'Settings') `
        -ExpectedParent $candidateRoot `
        -Label 'staged FoxMouse Settings directory'
    $candidateSettings = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $candidateSettingsRoot 'FoxMouse.Settings.exe') `
        -ExpectedParent $candidateSettingsRoot `
        -Label 'staged FoxMouse.Settings.exe'

    foreach ($copyPair in @(
        @($payloadApp, $candidateApp),
        @($payloadGuard, $candidateGuard),
        @($payloadSettings, $candidateSettings))) {
        $sourceHash = (Get-FileHash -LiteralPath $copyPair[0] -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $copyPair[1] -Algorithm SHA256).Hash
        if (-not [string]::Equals($sourceHash, $destinationHash, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Staged payload hash mismatch: $($copyPair[1])"
        }
    }

    if ($null -ne $expectedThumbprint) {
        Assert-FoxMouseAuthenticodeSignature `
            -LiteralPath $candidateApp `
            -ExpectedSignerThumbprint $expectedThumbprint
        Assert-FoxMouseAuthenticodeSignature `
            -LiteralPath $candidateGuard `
            -ExpectedSignerThumbprint $expectedThumbprint
        Assert-FoxMouseAuthenticodeSignature `
            -LiteralPath $candidateSettings `
            -ExpectedSignerThumbprint $expectedThumbprint
    }

    $preexistingProcesses = @(Get-FoxMouseProductProcesses -InstallRoot $installRoot)
    $previousAppWasRunning = @($preexistingProcesses | Where-Object { $_.Role -eq 'FoxMouse' }).Count -gt 0
    foreach ($entry in $preexistingProcesses) {
        $entry.Process.Dispose()
    }

    Stop-FoxMouseProductProcessesSafely `
        -InstallRoot $installRoot `
        -RecoveryExecutable $payloadGuard

    if (Test-Path -LiteralPath $shortcutPath) {
        $null = Assert-FoxMouseFile -LiteralPath $shortcutPath -ExpectedParent $startMenuRoot -Label 'existing shortcut'
        Copy-Item -LiteralPath $shortcutPath -Destination $shortcutBackup -Force -ErrorAction Stop
        $previousShortcutExists = $true
    }

    if (Test-Path -LiteralPath $installRoot) {
        $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $installRoot -Label 'install root'
        Move-FoxMouseDirectory `
            -Source $installRoot `
            -SourceParent $programsRoot `
            -Destination $backupRoot `
            -DestinationParent $programsRoot `
            -Label 'existing installation backup'
        $oldInstallBackedUp = $true
    }

    Move-FoxMouseDirectory `
        -Source $candidateRoot `
        -SourceParent $programsRoot `
        -Destination $installRoot `
        -DestinationParent $programsRoot `
        -Label 'new installation activation'
    $newInstallActivated = $true

    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = Join-Path $installRoot 'FoxMouse.exe'
        $shortcut.WorkingDirectory = $installRoot
        $shortcut.Description = 'FoxMouse - shake to find your cursor'
        $shortcut.Save()
    }
    finally {
        if ($null -ne $shell -and [Runtime.InteropServices.Marshal]::IsComObject($shell)) {
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
    }

    if (-not $NoLaunch) {
        $launchedProcess = Start-Process `
            -FilePath (Join-Path $installRoot 'FoxMouse.exe') `
            -ArgumentList '--background' `
            -PassThru
        Start-Sleep -Milliseconds 500
        if ($launchedProcess.HasExited) {
            throw "FoxMouse exited during startup with code $($launchedProcess.ExitCode)."
        }
    }

    $committed = $true
}
catch {
    $originalFailure = $_.Exception.Message
    $rollbackFailures = New-Object 'System.Collections.Generic.List[string]'

    if ($null -ne $launchedProcess) {
        try {
            if (-not $launchedProcess.HasExited -and $null -ne $payloadGuard) {
                Stop-FoxMouseProductProcessesSafely `
                    -InstallRoot $installRoot `
                    -RecoveryExecutable $payloadGuard
            }
        }
        catch {
            $rollbackFailures.Add("could not stop the newly launched version: $($_.Exception.Message)")
        }
    }

    if ($newInstallActivated -and (Test-Path -LiteralPath $installRoot)) {
        try {
            if (Test-Path -LiteralPath $candidateRoot) {
                throw "rollback candidate already exists: $candidateRoot"
            }
            Move-FoxMouseDirectory `
                -Source $installRoot `
                -SourceParent $programsRoot `
                -Destination $candidateRoot `
                -DestinationParent $programsRoot `
                -Label 'failed new installation rollback'
            $newInstallActivated = $false
        }
        catch {
            $rollbackFailures.Add("could not quarantine the failed new installation: $($_.Exception.Message)")
        }
    }

    if ($oldInstallBackedUp -and
        (Test-Path -LiteralPath $backupRoot) -and
        -not (Test-Path -LiteralPath $installRoot)) {
        try {
            Move-FoxMouseDirectory `
                -Source $backupRoot `
                -SourceParent $programsRoot `
                -Destination $installRoot `
                -DestinationParent $programsRoot `
                -Label 'existing installation rollback'
            $oldInstallBackedUp = $false
        }
        catch {
            $rollbackFailures.Add("could not restore the previous installation: $($_.Exception.Message)")
        }
    }

    try {
        if ($previousShortcutExists) {
            Copy-Item -LiteralPath $shortcutBackup -Destination $shortcutPath -Force -ErrorAction Stop
        }
        elseif (Test-Path -LiteralPath $shortcutPath) {
            $null = Assert-FoxMouseFile -LiteralPath $shortcutPath -ExpectedParent $startMenuRoot -Label 'new shortcut'
            Remove-Item -LiteralPath $shortcutPath -Force -ErrorAction Stop
        }
    }
    catch {
        $rollbackFailures.Add("could not restore the Start Menu shortcut: $($_.Exception.Message)")
    }

    if ($previousAppWasRunning -and
        -not $newInstallActivated -and
        (Test-Path -LiteralPath (Join-Path $installRoot 'FoxMouse.exe') -PathType Leaf)) {
        try {
            $restoredProcess = Start-Process `
                -FilePath (Join-Path $installRoot 'FoxMouse.exe') `
                -ArgumentList '--background' `
                -PassThru
            Start-Sleep -Milliseconds 500
            if ($restoredProcess.HasExited -and $restoredProcess.ExitCode -ne 0) {
                throw "restored FoxMouse exited with code $($restoredProcess.ExitCode)"
            }
            $restoredProcess.Dispose()
        }
        catch {
            $rollbackFailures.Add("could not restart the previous version: $($_.Exception.Message)")
        }
    }

    $message = "FoxMouse installation failed: $originalFailure"
    if ($rollbackFailures.Count -gt 0) {
        $message += ' Rollback also reported: ' + ($rollbackFailures -join '; ')
    }
    throw $message
}
finally {
    if ($null -ne $launchedProcess) {
        $launchedProcess.Dispose()
    }

    if ($committed -and (Test-Path -LiteralPath $backupRoot)) {
        try {
            Remove-FoxMouseTree `
                -LiteralPath $backupRoot `
                -ExpectedParent $programsRoot `
                -Label 'committed installation backup'
        }
        catch {
            Write-Warning "The previous-version backup was retained: $($_.Exception.Message)"
        }
    }

    if (Test-Path -LiteralPath $candidateRoot) {
        try {
            Remove-FoxMouseTree `
                -LiteralPath $candidateRoot `
                -ExpectedParent $programsRoot `
                -Label 'unused install candidate'
        }
        catch {
            Write-Warning "The unused install candidate was retained: $($_.Exception.Message)"
        }
    }

    if (Test-Path -LiteralPath $stagingRoot) {
        try {
            Remove-FoxMouseTree `
                -LiteralPath $stagingRoot `
                -ExpectedParent $tempRoot `
                -Label 'staging root'
        }
        catch {
            Write-Warning "The temporary staging directory was retained: $($_.Exception.Message)"
        }
    }
}

Write-Output "FoxMouse installed to: $installRoot"
