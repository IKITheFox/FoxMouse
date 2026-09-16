[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$scriptRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
. (Join-Path $scriptRoot 'FoxMouse.ScriptSafety.ps1')

$scriptsToParse = @(
    'FoxMouse.ScriptSafety.ps1',
    'Install-FoxMouse.ps1',
    'Uninstall-FoxMouse.ps1',
    'New-Release.ps1',
    'Get-DotNet.ps1',
    'Test-InstallerLifecycle.ps1',
    'Test-ReleaseArtifact.ps1',
    'Write-ValidationEnvironment.ps1',
    'Accept-Milestone.ps1',
    'Generate-FoxMouseIcon.ps1',
    'Generate-BrandingAssets.ps1'
)
foreach ($scriptName in $scriptsToParse) {
    $tokens = $null
    $parseErrors = $null
    $scriptPath = Join-Path $scriptRoot $scriptName
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $scriptPath,
        [ref]$tokens,
        [ref]$parseErrors)
    if (@($parseErrors).Count -gt 0) {
        $messages = (@($parseErrors) | ForEach-Object { $_.Message }) -join '; '
        throw "PowerShell syntax validation failed for '$scriptPath': $messages"
    }
}

$tempRoot = Get-FoxMouseFullPath -LiteralPath ([IO.Path]::GetTempPath())
$null = Assert-FoxMouseDirectoryRoot -LiteralPath $tempRoot -Label 'test temporary root'
$runRoot = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $tempRoot ("FoxMouse-DeploymentSafety-" + [Guid]::NewGuid().ToString('N'))) `
    -ExpectedParent $tempRoot `
    -Label 'test run root'
$junctionRoot = $null

[void][IO.Directory]::CreateDirectory($runRoot)
$null = Assert-FoxMouseDirectoryRoot `
    -LiteralPath $runRoot `
    -ExpectedParent $tempRoot `
    -Label 'test run root'

try {
    $deleteTree = Join-Path $runRoot 'delete-me'
    [void][IO.Directory]::CreateDirectory($deleteTree)
    Set-Content -LiteralPath (Join-Path $deleteTree 'marker.txt') -Value 'safe-delete-test' -Encoding ascii
    Remove-FoxMouseTree -LiteralPath $deleteTree -ExpectedParent $runRoot -Label 'safe delete test'
    if (Test-Path -LiteralPath $deleteTree) {
        throw 'Safe delete test did not remove its child tree.'
    }

    $moveSource = Join-Path $runRoot 'move-source'
    $moveDestination = Join-Path $runRoot 'move-destination'
    [void][IO.Directory]::CreateDirectory($moveSource)
    Set-Content -LiteralPath (Join-Path $moveSource 'marker.txt') -Value 'safe-move-test' -Encoding ascii
    Move-FoxMouseDirectory `
        -Source $moveSource `
        -SourceParent $runRoot `
        -Destination $moveDestination `
        -DestinationParent $runRoot `
        -Label 'safe move test'
    if (-not (Test-Path -LiteralPath (Join-Path $moveDestination 'marker.txt') -PathType Leaf)) {
        throw 'Safe move test did not preserve its marker.'
    }

    # Mirror New-Release's pre-activation failure path: preserve the old
    # release as a sibling backup, force activation to fail, then restore it.
    $releaseTransactionRoot = Join-Path $runRoot 'release-transaction'
    [void][IO.Directory]::CreateDirectory($releaseTransactionRoot)
    $releaseActive = Join-Path $releaseTransactionRoot 'v0.1.0'
    $releaseCandidate = Join-Path $releaseTransactionRoot 'candidate'
    $releaseBackup = Join-Path $releaseTransactionRoot '.v0.1.0.backup.test'
    [void][IO.Directory]::CreateDirectory($releaseActive)
    [void][IO.Directory]::CreateDirectory($releaseCandidate)
    Set-Content -LiteralPath (Join-Path $releaseActive 'old-marker.txt') -Value 'old-release' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $releaseCandidate 'new-marker.txt') -Value 'new-release' -Encoding ascii

    Move-FoxMouseDirectory `
        -Source $releaseActive `
        -SourceParent $releaseTransactionRoot `
        -Destination $releaseBackup `
        -DestinationParent $releaseTransactionRoot `
        -Label 'release rollback test backup'

    # Occupying the activation path makes the candidate rename fail without
    # mutating either the candidate or the backup.
    [void][IO.Directory]::CreateDirectory($releaseActive)
    $activationFailed = $false
    try {
        Move-FoxMouseDirectory `
            -Source $releaseCandidate `
            -SourceParent $releaseTransactionRoot `
            -Destination $releaseActive `
            -DestinationParent $releaseTransactionRoot `
            -Label 'release rollback test activation'
    }
    catch {
        $activationFailed = $true
    }
    if (-not $activationFailed) {
        throw 'The release rollback test could not force the activation rename to fail.'
    }

    Remove-FoxMouseTree `
        -LiteralPath $releaseActive `
        -ExpectedParent $releaseTransactionRoot `
        -Label 'release rollback test blocker'
    Move-FoxMouseDirectory `
        -Source $releaseBackup `
        -SourceParent $releaseTransactionRoot `
        -Destination $releaseActive `
        -DestinationParent $releaseTransactionRoot `
        -Label 'release rollback test restore'
    if ((Get-Content -LiteralPath (Join-Path $releaseActive 'old-marker.txt') -Raw).Trim() -ne 'old-release' -or
        -not (Test-Path -LiteralPath (Join-Path $releaseCandidate 'new-marker.txt') -PathType Leaf)) {
        throw 'The release rollback test did not restore the old release intact.'
    }

    $rootDeleteRejected = $false
    try {
        Remove-FoxMouseTree -LiteralPath $runRoot -ExpectedParent $runRoot -Label 'root rejection test'
    }
    catch {
        $rootDeleteRejected = $true
    }
    if (-not $rootDeleteRejected -or -not (Test-Path -LiteralPath $runRoot -PathType Container)) {
        throw 'The delete guard did not reject deleting its expected parent root.'
    }

    $junctionTarget = Join-Path $runRoot 'junction-target'
    $junctionRoot = Join-Path $runRoot 'junction-root'
    [void][IO.Directory]::CreateDirectory($junctionTarget)
    New-Item -ItemType Junction -Path $junctionRoot -Target $junctionTarget -ErrorAction Stop | Out-Null

    $junctionRejected = $false
    try {
        $null = Assert-FoxMouseDirectoryRoot `
            -LiteralPath $junctionRoot `
            -ExpectedParent $runRoot `
            -Label 'junction rejection test'
    }
    catch {
        $junctionRejected = $true
    }
    if (-not $junctionRejected) {
        throw 'The root guard accepted a directory junction.'
    }

    Remove-Item -LiteralPath $junctionRoot -Force -ErrorAction Stop
    $junctionRoot = $null

    $thumbprint = Normalize-FoxMouseThumbprint -Thumbprint ((('ab' * 20) -join ''))
    if ($thumbprint -ne (('AB' * 20) -join '')) {
        throw 'Thumbprint normalization returned an unexpected value.'
    }

    $invalidThumbprintRejected = $false
    try {
        $null = Normalize-FoxMouseThumbprint -Thumbprint 'not-a-thumbprint'
    }
    catch {
        $invalidThumbprintRejected = $true
    }
    if (-not $invalidThumbprintRejected) {
        throw 'Invalid certificate thumbprint input was accepted.'
    }

    Add-Type -AssemblyName System.IO.Compression -ErrorAction Stop
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    $zipDestination = Join-Path $runRoot 'zip-destination'
    [void][IO.Directory]::CreateDirectory($zipDestination)

    $validZipPath = Join-Path $runRoot 'valid.zip'
    $validZip = [IO.Compression.ZipFile]::Open(
        $validZipPath,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryName in @(
            'FoxMouse/FoxMouse.exe',
            'FoxMouse/FoxMouse.Guard.exe',
            'FoxMouse/Settings/FoxMouse.Settings.exe')) {
            $entry = $validZip.CreateEntry($entryName)
            $writer = New-Object IO.StreamWriter ($entry.Open())
            try {
                $writer.Write('placeholder')
            }
            finally {
                $writer.Dispose()
            }
        }
    }
    finally {
        $validZip.Dispose()
    }
    Assert-FoxMouseZipEntriesSafe -ArchivePath $validZipPath -DestinationRoot $zipDestination

    $traversalZipPath = Join-Path $runRoot 'traversal.zip'
    $traversalZip = [IO.Compression.ZipFile]::Open(
        $traversalZipPath,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $traversalZip.CreateEntry('../outside.txt')
        $writer = New-Object IO.StreamWriter ($entry.Open())
        try {
            $writer.Write('must-not-extract')
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $traversalZip.Dispose()
    }

    $traversalRejected = $false
    try {
        Assert-FoxMouseZipEntriesSafe `
            -ArchivePath $traversalZipPath `
            -DestinationRoot $zipDestination
    }
    catch {
        $traversalRejected = $true
    }
    if (-not $traversalRejected -or (Test-Path -LiteralPath (Join-Path $runRoot 'outside.txt'))) {
        throw 'The ZIP guard accepted or extracted a traversal entry.'
    }
}
finally {
    if ($null -ne $junctionRoot -and (Test-Path -LiteralPath $junctionRoot)) {
        Remove-Item -LiteralPath $junctionRoot -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $runRoot) {
        Remove-FoxMouseTree `
            -LiteralPath $runRoot `
            -ExpectedParent $tempRoot `
            -Label 'test run root cleanup'
    }
}

Write-Output 'FoxMouse deployment script safety checks passed.'
