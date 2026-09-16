[CmdletBinding()]
param([Parameter(Mandatory)][string]$SetupDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FoxMouse.ScriptSafety.ps1')
$taskSetupRoot = Assert-FoxMouseDirectoryRoot -LiteralPath $SetupDirectory -Label 'online setup'
$taskSetup = Assert-FoxMouseFile -LiteralPath (Join-Path $taskSetupRoot 'FoxMouse-Setup-x64.exe') -ExpectedParent $taskSetupRoot -Label 'setup'
$taskPackage = Assert-FoxMouseFile -LiteralPath (Join-Path $taskSetupRoot 'FoxMouse-package.zip') -ExpectedParent $taskSetupRoot -Label 'payload'
$taskId = [Guid]::NewGuid().ToString('N')
$taskTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$taskRoot = Join-Path $taskTemp "FoxMouse-InstallerLifecycle-$taskId"
$null = Assert-FoxMouseChildPath -LiteralPath $taskRoot -ExpectedParent $taskTemp -Label 'isolated lifecycle'
$taskRegistry = "Software\FoxMouse\Tests\InstallerLifecycle-$taskId"
$taskCommon = @('--test-root', $taskRoot, '--test-registry-root', $taskRegistry, '--quiet', '--no-launch')
$taskPrevious = [Environment]::GetEnvironmentVariable('FOXMOUSE_ENABLE_ISOLATED_DEPLOYMENT_TESTS', 'Process')
function Invoke-OnlineTestProcess([string]$Executable, [string[]]$CommandArguments) {
    $taskResultFile = $null
    if ([IO.Path]::GetFileName($Executable) -eq 'FoxMouse.Uninstall.exe') {
        $taskResultFile = Join-Path $taskRoot ('result-' + [Guid]::NewGuid().ToString('N') + '.json')
        $CommandArguments = @($CommandArguments) + @('--result-file', $taskResultFile)
    }
    $taskInfo = New-FoxMouseProcessStartInfo -Executable $Executable -ArgumentList $CommandArguments -Hidden
    if ($CommandArguments -contains '--window-smoke-test') { $taskInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal }
    $taskProcess = [Diagnostics.Process]::Start($taskInfo)
    try {
        if (!$taskProcess.WaitForExit(60000)) { throw "Timed out; process $($taskProcess.Id) must be inspected before cleanup." }
        if ($taskProcess.ExitCode -ne 0) { throw "Process failed with exit code $($taskProcess.ExitCode): $Executable" }
    } finally { $taskProcess.Dispose() }
    if ($null -ne $taskResultFile) {
        $taskDeadline = [DateTime]::UtcNow.AddSeconds(60)
        while (!(Test-Path -LiteralPath $taskResultFile) -and [DateTime]::UtcNow -lt $taskDeadline) { Start-Sleep -Milliseconds 100 }
        if (!(Test-Path -LiteralPath $taskResultFile)) { throw 'Detached maintenance produced no result.' }
        $taskResult = Get-Content -LiteralPath $taskResultFile -Raw | ConvertFrom-Json
        if ($taskResult.exitCode -ne 0) { throw "Detached maintenance failed: $($taskResult.operationError)" }
    }
}
try {
    [Environment]::SetEnvironmentVariable('FOXMOUSE_ENABLE_ISOLATED_DEPLOYMENT_TESTS', '1', 'Process')
    Invoke-OnlineTestProcess $taskSetup (@('--install', '--package', $taskPackage, '--language', 'en-US') + $taskCommon)
    $taskInstall = Join-Path $taskRoot 'Programs/FoxMouse'
    $taskSettingsPath = Join-Path $taskRoot 'LocalAppData/FoxMouse/settings.json'
    $taskSettingsBefore = Get-Content -LiteralPath $taskSettingsPath -Raw
    if (($taskSettingsBefore | ConvertFrom-Json).language -ne 'en-US') { throw 'Installer language was not persisted for first launch.' }
    $taskMaintenance = Join-Path $taskRoot 'LocalAppData/FoxMouse/Maintenance/FoxMouse.Uninstall.exe'
    $null = Assert-FoxMouseFile -LiteralPath $taskMaintenance -ExpectedParent $taskRoot -Label 'detached maintenance'
    $taskLanguageMetrics = Join-Path $taskRoot 'maintenance-language.json'
    Invoke-OnlineTestProcess $taskMaintenance @('--test-root', $taskRoot, '--test-registry-root', $taskRegistry,
        '--window-smoke-test', 'idle', '--window-smoke-metrics', $taskLanguageMetrics, '--no-launch')
    $taskLanguageEvidence = Get-Content -LiteralPath $taskLanguageMetrics -Raw | ConvertFrom-Json
    if ($taskLanguageEvidence.displayLanguage -ne 'en-US' -or $taskLanguageEvidence.title -ne 'FoxMouse Maintenance') {
        throw "Maintenance did not load the saved language: $($taskLanguageEvidence.title)"
    }
    $taskReadme = Assert-FoxMouseFile -LiteralPath (Join-Path $taskInstall 'README.md') -ExpectedParent $taskRoot -Label 'isolated repair fixture'
    $taskHash = (Get-FileHash -LiteralPath $taskReadme).Hash
    # Remove only our newly installed test file to exercise the cached repair.
    Remove-Item -LiteralPath $taskReadme
    Invoke-OnlineTestProcess $taskMaintenance (@('--repair') + $taskCommon)
    if ((Get-FileHash -LiteralPath $taskReadme).Hash -ne $taskHash) { throw 'Cached repair did not restore the original file.' }
    if ((Get-Content -LiteralPath $taskSettingsPath -Raw) -ne $taskSettingsBefore) { throw 'Repair changed the saved settings.' }
    Invoke-OnlineTestProcess $taskMaintenance (@('--uninstall', '--delete-settings') + $taskCommon)
    if (Test-Path -LiteralPath $taskInstall) { throw 'Uninstall retained the application directory.' }
    if (Test-Path -LiteralPath "HKCU:\$taskRegistry\Uninstall\FoxMouse") { throw 'Uninstall retained the application registration.' }
    [pscustomobject]@{ Result = 'Passed'; Scope = 'CLI install, detached cached repair, uninstall'; EvidenceRoot = $taskRoot }
} finally {
    [Environment]::SetEnvironmentVariable('FOXMOUSE_ENABLE_ISOLATED_DEPLOYMENT_TESTS', $taskPrevious, 'Process')
    # Preserve the isolated directory on failure for diagnosis; no blanket deletion.
}
