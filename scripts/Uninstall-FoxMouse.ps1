[CmdletBinding()]
param(
    [switch]$KeepSettings,
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
if (Test-Path -LiteralPath $programsRoot) {
    $null = Assert-FoxMouseDirectoryRoot `
        -LiteralPath $programsRoot `
        -ExpectedParent $localAppData `
        -Label 'Programs root'
}

$installRoot = Get-FoxMouseFullPath -LiteralPath (Join-Path $programsRoot 'FoxMouse')
$null = Assert-FoxMouseChildPath -LiteralPath $installRoot -ExpectedParent $programsRoot -Label 'install root'

$settingsRoot = Get-FoxMouseFullPath -LiteralPath (Join-Path $localAppData 'FoxMouse')
$null = Assert-FoxMouseChildPath -LiteralPath $settingsRoot -ExpectedParent $localAppData -Label 'settings root'

$startMenuRoot = Get-FoxMouseFullPath -LiteralPath ([Environment]::GetFolderPath(
    [Environment+SpecialFolder]::Programs))
$null = Assert-FoxMouseDirectoryRoot -LiteralPath $startMenuRoot -Label 'Start Menu Programs root'
$shortcutPath = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $startMenuRoot 'FoxMouse.lnk') `
    -ExpectedParent $startMenuRoot `
    -Label 'Start Menu shortcut'

$expectedThumbprint = $null
if (-not [string]::IsNullOrWhiteSpace($ExpectedSignerThumbprint)) {
    $expectedThumbprint = Normalize-FoxMouseThumbprint -Thumbprint $ExpectedSignerThumbprint
}

$transactionId = [Guid]::NewGuid().ToString('N')
$installQuarantine = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $programsRoot ".FoxMouse.uninstall.$transactionId") `
    -ExpectedParent $programsRoot `
    -Label 'uninstall quarantine'
$settingsQuarantine = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $localAppData ".FoxMouse.settings-uninstall.$transactionId") `
    -ExpectedParent $localAppData `
    -Label 'settings quarantine'
$shortcutBackup = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $startMenuRoot ".FoxMouse.lnk.uninstall.$transactionId") `
    -ExpectedParent $startMenuRoot `
    -Label 'shortcut quarantine'

$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'FoxMouse'
$runValueExisted = $false
$runValueData = $null
$runValueKind = $null
$installMoved = $false
$settingsMoved = $false
$shortcutMoved = $false
$committed = $false

try {
    if (Test-Path -LiteralPath $installRoot) {
        if (-not (Test-Path -LiteralPath $programsRoot)) {
            throw 'The install root exists while its expected parent is unavailable.'
        }

        $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $installRoot -Label 'install root'
        $installedApp = Assert-FoxMouseFile `
            -LiteralPath (Join-Path $installRoot 'FoxMouse.exe') `
            -ExpectedParent $installRoot `
            -Label 'installed FoxMouse.exe'
        $installedGuard = Assert-FoxMouseFile `
            -LiteralPath (Join-Path $installRoot 'FoxMouse.Guard.exe') `
            -ExpectedParent $installRoot `
            -Label 'installed FoxMouse.Guard.exe'
        $installedSettings = $null
        $installedSettingsRoot = Join-Path $installRoot 'Settings'
        if (Test-Path -LiteralPath $installedSettingsRoot) {
            $installedSettingsRoot = Assert-FoxMouseDirectoryRoot `
                -LiteralPath $installedSettingsRoot `
                -ExpectedParent $installRoot `
                -Label 'installed FoxMouse Settings directory'
            $installedSettings = Assert-FoxMouseFile `
                -LiteralPath (Join-Path $installedSettingsRoot 'FoxMouse.Settings.exe') `
                -ExpectedParent $installedSettingsRoot `
                -Label 'installed FoxMouse.Settings.exe'
        }

        if ($null -ne $expectedThumbprint) {
            Assert-FoxMouseAuthenticodeSignature `
                -LiteralPath $installedApp `
                -ExpectedSignerThumbprint $expectedThumbprint
            Assert-FoxMouseAuthenticodeSignature `
                -LiteralPath $installedGuard `
                -ExpectedSignerThumbprint $expectedThumbprint
            if ($null -ne $installedSettings) {
                Assert-FoxMouseAuthenticodeSignature `
                    -LiteralPath $installedSettings `
                    -ExpectedSignerThumbprint $expectedThumbprint
            }
        }

        Stop-FoxMouseProductProcessesSafely `
            -InstallRoot $installRoot `
            -RecoveryExecutable $installedGuard

        $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $installRoot -Label 'install root'
        Move-FoxMouseDirectory `
            -Source $installRoot `
            -SourceParent $programsRoot `
            -Destination $installQuarantine `
            -DestinationParent $programsRoot `
            -Label 'installation quarantine'
        $installMoved = $true
    }

    if (-not $KeepSettings -and (Test-Path -LiteralPath $settingsRoot)) {
        $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $settingsRoot -Label 'settings root'
        Move-FoxMouseDirectory `
            -Source $settingsRoot `
            -SourceParent $localAppData `
            -Destination $settingsQuarantine `
            -DestinationParent $localAppData `
            -Label 'settings quarantine'
        $settingsMoved = $true
    }

    if (Test-Path -LiteralPath $runKeyPath) {
        $runKey = Get-Item -LiteralPath $runKeyPath -ErrorAction Stop
        try {
            if ($runKey.GetValueNames() -contains $runValueName) {
                $runValueExisted = $true
                $runValueData = $runKey.GetValue(
                    $runValueName,
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                $runValueKind = $runKey.GetValueKind($runValueName)
                Remove-ItemProperty `
                    -LiteralPath $runKeyPath `
                    -Name $runValueName `
                    -ErrorAction Stop
            }
        }
        finally {
            $runKey.Dispose()
        }
    }

    if (Test-Path -LiteralPath $shortcutPath) {
        $null = Assert-FoxMouseFile `
            -LiteralPath $shortcutPath `
            -ExpectedParent $startMenuRoot `
            -Label 'Start Menu shortcut'
        if (Test-Path -LiteralPath $shortcutBackup) {
            throw "Shortcut quarantine unexpectedly exists: $shortcutBackup"
        }
        [IO.File]::Move($shortcutPath, $shortcutBackup)
        $shortcutMoved = $true
    }

    $committed = $true
}
catch {
    $originalFailure = $_.Exception.Message
    $rollbackFailures = New-Object 'System.Collections.Generic.List[string]'

    if ($shortcutMoved -and (Test-Path -LiteralPath $shortcutBackup)) {
        try {
            if (Test-Path -LiteralPath $shortcutPath) {
                throw "cannot restore shortcut because destination exists: $shortcutPath"
            }
            [IO.File]::Move($shortcutBackup, $shortcutPath)
            $shortcutMoved = $false
        }
        catch {
            $rollbackFailures.Add("could not restore the shortcut: $($_.Exception.Message)")
        }
    }

    try {
        if ($runValueExisted) {
            if (-not (Test-Path -LiteralPath $runKeyPath)) {
                New-Item -Path $runKeyPath -Force -ErrorAction Stop | Out-Null
            }
            New-ItemProperty `
                -LiteralPath $runKeyPath `
                -Name $runValueName `
                -Value $runValueData `
                -PropertyType $runValueKind `
                -Force `
                -ErrorAction Stop | Out-Null
        }
    }
    catch {
        $rollbackFailures.Add("could not restore the startup registration: $($_.Exception.Message)")
    }

    if ($settingsMoved -and
        (Test-Path -LiteralPath $settingsQuarantine) -and
        -not (Test-Path -LiteralPath $settingsRoot)) {
        try {
            Move-FoxMouseDirectory `
                -Source $settingsQuarantine `
                -SourceParent $localAppData `
                -Destination $settingsRoot `
                -DestinationParent $localAppData `
                -Label 'settings rollback'
            $settingsMoved = $false
        }
        catch {
            $rollbackFailures.Add("could not restore settings: $($_.Exception.Message)")
        }
    }

    if ($installMoved -and
        (Test-Path -LiteralPath $installQuarantine) -and
        -not (Test-Path -LiteralPath $installRoot)) {
        try {
            Move-FoxMouseDirectory `
                -Source $installQuarantine `
                -SourceParent $programsRoot `
                -Destination $installRoot `
                -DestinationParent $programsRoot `
                -Label 'installation rollback'
            $installMoved = $false
        }
        catch {
            $rollbackFailures.Add("could not restore the installation: $($_.Exception.Message)")
        }
    }

    $message = "FoxMouse uninstall failed: $originalFailure"
    if ($rollbackFailures.Count -gt 0) {
        $message += ' Rollback also reported: ' + ($rollbackFailures -join '; ')
    }
    throw $message
}
finally {
    if ($committed -and (Test-Path -LiteralPath $installQuarantine)) {
        try {
            Remove-FoxMouseTree `
                -LiteralPath $installQuarantine `
                -ExpectedParent $programsRoot `
                -Label 'uninstalled application quarantine'
        }
        catch {
            Write-Warning "The uninstalled application quarantine was retained: $($_.Exception.Message)"
        }
    }

    if ($committed -and (Test-Path -LiteralPath $settingsQuarantine)) {
        try {
            Remove-FoxMouseTree `
                -LiteralPath $settingsQuarantine `
                -ExpectedParent $localAppData `
                -Label 'uninstalled settings quarantine'
        }
        catch {
            Write-Warning "The uninstalled settings quarantine was retained: $($_.Exception.Message)"
        }
    }

    if ($committed -and (Test-Path -LiteralPath $shortcutBackup)) {
        try {
            $null = Assert-FoxMouseFile `
                -LiteralPath $shortcutBackup `
                -ExpectedParent $startMenuRoot `
                -Label 'uninstalled shortcut quarantine'
            Remove-Item -LiteralPath $shortcutBackup -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "The uninstalled shortcut quarantine was retained: $($_.Exception.Message)"
        }
    }
}

Write-Output 'FoxMouse was uninstalled successfully.'
