[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipValidation,
    [string]$CertificateThumbprint,
    [string]$ExpectedVersion,
    [switch]$CandidateOnly
)

$ErrorActionPreference = 'Stop'
$scriptRoot = [IO.Path]::GetFullPath((Split-Path -Parent $MyInvocation.MyCommand.Path))
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $scriptRoot))
. (Join-Path $scriptRoot 'FoxMouse.ScriptSafety.ps1')

# The initial .NET path calls deliberately avoid relying on the helper before
# it has been loaded. Re-normalize both roots with the shared implementation.
$scriptRoot = Get-FoxMouseFullPath -LiteralPath $scriptRoot
$projectRoot = Get-FoxMouseFullPath -LiteralPath $projectRoot
$null = Assert-FoxMouseDirectoryRoot -LiteralPath $projectRoot -Label 'project root'
$null = Assert-FoxMouseDirectoryRoot `
    -LiteralPath $scriptRoot `
    -ExpectedParent $projectRoot `
    -Label 'scripts root'

. (Join-Path $scriptRoot 'Get-DotNet.ps1')
$dotnet = Get-FoxMouseDotNet
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$version = [string]$buildProperties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Directory.Build.props must define a three-part numeric Version; found '$version'."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "ExpectedVersion must be a three-part numeric version; found '$ExpectedVersion'."
    }
    if (-not [string]::Equals($ExpectedVersion, $version, [StringComparison]::Ordinal)) {
        throw "Release version mismatch: expected '$ExpectedVersion', but Directory.Build.props defines '$version'."
    }
}
$ExpectedVersion = $version
$releaseDirectoryName = if ($CandidateOnly) { "v$version-candidate-$([Guid]::NewGuid().ToString('N'))" } else { "v$version" }
$normalizedThumbprint = $null
if (-not [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $normalizedThumbprint = Normalize-FoxMouseThumbprint -Thumbprint $CertificateThumbprint
}

$artifactsRoot = Get-FoxMouseFullPath -LiteralPath (Join-Path $projectRoot 'artifacts')
$null = Assert-FoxMouseChildPath `
    -LiteralPath $artifactsRoot `
    -ExpectedParent $projectRoot `
    -Label 'artifacts root'
if (-not (Test-Path -LiteralPath $artifactsRoot)) {
    [void][IO.Directory]::CreateDirectory($artifactsRoot)
}
$null = Assert-FoxMouseDirectoryRoot `
    -LiteralPath $artifactsRoot `
    -ExpectedParent $projectRoot `
    -Label 'artifacts root'

$releaseParent = Get-FoxMouseFullPath -LiteralPath (Join-Path $artifactsRoot 'release')
$null = Assert-FoxMouseChildPath `
    -LiteralPath $releaseParent `
    -ExpectedParent $artifactsRoot `
    -Label 'release parent'
if (-not (Test-Path -LiteralPath $releaseParent)) {
    [void][IO.Directory]::CreateDirectory($releaseParent)
}
$null = Assert-FoxMouseDirectoryRoot `
    -LiteralPath $releaseParent `
    -ExpectedParent $artifactsRoot `
    -Label 'release parent'

# A new version is published beside earlier versions. Record the inexpensive
# directory metadata plus the two signed-off metadata files for every earlier
# release, then prove packaging did not rewrite or remove that evidence.
$historicalReleaseSnapshot = @(
    Get-ChildItem -LiteralPath $releaseParent -Directory -Force |
        Where-Object {
            $_.Name -match '^v\d+\.\d+\.\d+$' -and
            ($CandidateOnly -or -not [string]::Equals($_.Name, "v$version", [StringComparison]::OrdinalIgnoreCase))
        } |
        Sort-Object Name |
        ForEach-Object {
            $historyManifest = Assert-FoxMouseFile `
                -LiteralPath (Join-Path $_.FullName 'release-manifest.json') `
                -ExpectedParent $_.FullName `
                -Label "historical release manifest $($_.Name)"
            $historyHashes = Assert-FoxMouseFile `
                -LiteralPath (Join-Path $_.FullName 'SHA256SUMS.txt') `
                -ExpectedParent $_.FullName `
                -Label "historical release hash list $($_.Name)"
            [pscustomobject]@{
                Name = $_.Name
                Path = $_.FullName
                ManifestSha256 = (Get-FileHash -LiteralPath $historyManifest -Algorithm SHA256).Hash
                HashListSha256 = (Get-FileHash -LiteralPath $historyHashes -Algorithm SHA256).Hash
                FileCount = @(Get-ChildItem -LiteralPath $_.FullName -File -Force).Count
            }
        }
)

function Assert-HistoricalReleaseSnapshot {
    foreach ($historicalRelease in $historicalReleaseSnapshot) {
        $historicalRoot = Assert-FoxMouseDirectoryRoot `
            -LiteralPath $historicalRelease.Path `
            -ExpectedParent $releaseParent `
            -Label "historical release $($historicalRelease.Name)"
        $historicalManifest = Assert-FoxMouseFile `
            -LiteralPath (Join-Path $historicalRoot 'release-manifest.json') `
            -ExpectedParent $historicalRoot `
            -Label "historical release manifest $($historicalRelease.Name)"
        $historicalHashes = Assert-FoxMouseFile `
            -LiteralPath (Join-Path $historicalRoot 'SHA256SUMS.txt') `
            -ExpectedParent $historicalRoot `
            -Label "historical release hash list $($historicalRelease.Name)"

        if ((Get-FileHash -LiteralPath $historicalManifest -Algorithm SHA256).Hash -ne $historicalRelease.ManifestSha256 -or
            (Get-FileHash -LiteralPath $historicalHashes -Algorithm SHA256).Hash -ne $historicalRelease.HashListSha256 -or
            @(Get-ChildItem -LiteralPath $historicalRoot -File -Force).Count -ne $historicalRelease.FileCount) {
            throw "Historical release evidence changed while publishing $version`: $($historicalRelease.Name)"
        }
    }
}

$releaseRoot = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $releaseParent $releaseDirectoryName) `
    -ExpectedParent $releaseParent `
    -Label 'release root'
if (Test-Path -LiteralPath $releaseRoot) {
    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $releaseRoot -Label 'release root'
}

$transactionId = [Guid]::NewGuid().ToString('N')
$workRoot = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $artifactsRoot ".FoxMouse.release-staging.$transactionId") `
    -ExpectedParent $artifactsRoot `
    -Label 'release staging root'
$candidateReleaseRoot = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $workRoot 'release-output') `
    -ExpectedParent $workRoot `
    -Label 'candidate release root'
$publishRoot = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $workRoot 'publish') `
    -ExpectedParent $workRoot `
    -Label 'publish staging root'
$releaseBackup = Assert-FoxMouseChildPath `
    -LiteralPath (Join-Path $releaseParent ".v$version.backup.$transactionId") `
    -ExpectedParent $releaseParent `
    -Label 'release backup'

[void][IO.Directory]::CreateDirectory($workRoot)
$null = Assert-FoxMouseDirectoryRoot `
    -LiteralPath $workRoot `
    -ExpectedParent $artifactsRoot `
    -Label 'release staging root'
[void][IO.Directory]::CreateDirectory($candidateReleaseRoot)
[void][IO.Directory]::CreateDirectory($publishRoot)
$null = Assert-FoxMouseDirectoryRoot `
    -LiteralPath $candidateReleaseRoot `
    -ExpectedParent $workRoot `
    -Label 'candidate release root'
$null = Assert-FoxMouseDirectoryRoot `
    -LiteralPath $publishRoot `
    -ExpectedParent $workRoot `
    -Label 'publish staging root'

$oldReleaseBackedUp = $false
$newReleaseActivated = $false
$committed = $false

function Invoke-FoxMouseSign {
    param(
        [Parameter(Mandatory)]
        [string]$SignTool,
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [Parameter(Mandatory)]
        [string]$SignerThumbprint
    )

    & $SignTool sign `
        /sha1 $SignerThumbprint `
        /fd SHA256 `
        /tr http://timestamp.digicert.com `
        /td SHA256 `
        $LiteralPath
    $signExitCode = $LASTEXITCODE
    if ($signExitCode -ne 0) {
        throw "Signing failed with exit code $signExitCode`: $LiteralPath"
    }

    Assert-FoxMouseAuthenticodeSignature `
        -LiteralPath $LiteralPath `
        -ExpectedSignerThumbprint $SignerThumbprint
}

try {
    if (-not $SkipValidation) {
        & $dotnet test (Join-Path $projectRoot 'FoxMouse.slnx') -c $Configuration --nologo
        $testExitCode = $LASTEXITCODE
        if ($testExitCode -ne 0) {
            throw "Tests failed with exit code $testExitCode; release packaging is blocked."
        }
    }

    $appPublish = Join-Path $publishRoot 'app'
    $guardPublish = Join-Path $publishRoot 'guard'
    $settingsPublish = Join-Path $publishRoot 'settings'
    $uninstallPublish = Join-Path $publishRoot 'uninstall'
    $cleanupPublish = Join-Path $publishRoot 'cleanup'
    & $dotnet publish (Join-Path $projectRoot 'src\FoxMouse.App\FoxMouse.App.csproj') `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -o $appPublish --nologo
    $appPublishExitCode = $LASTEXITCODE
    if ($appPublishExitCode -ne 0) {
        throw "FoxMouse publish failed with exit code $appPublishExitCode."
    }

    & $dotnet publish (Join-Path $projectRoot 'src\FoxMouse.Guard\FoxMouse.Guard.csproj') `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -o $guardPublish --nologo
    $guardPublishExitCode = $LASTEXITCODE
    if ($guardPublishExitCode -ne 0) {
        throw "FoxMouse.Guard publish failed with exit code $guardPublishExitCode."
    }

    & $dotnet publish (Join-Path $projectRoot 'src\FoxMouse.Settings\FoxMouse.Settings.csproj') `
        -c $Configuration -r win-x64 --self-contained true `
        -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -o $settingsPublish --nologo
    $settingsPublishExitCode = $LASTEXITCODE
    if ($settingsPublishExitCode -ne 0) {
        throw "FoxMouse.Settings publish failed with exit code $settingsPublishExitCode."
    }

    & $dotnet publish (Join-Path $projectRoot 'src\FoxMouse.Uninstall\FoxMouse.Uninstall.csproj') `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:PublishTrimmed=false -o $uninstallPublish --nologo
    $uninstallPublishExitCode = $LASTEXITCODE
    if ($uninstallPublishExitCode -ne 0) {
        throw "FoxMouse.Uninstall publish failed with exit code $uninstallPublishExitCode."
    }

    [void][IO.Directory]::CreateDirectory($cleanupPublish)
    $frameworkCompiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $frameworkCompiler -PathType Leaf)) {
        throw "The Windows .NET Framework cleanup-worker compiler was not found: $frameworkCompiler"
    }
    $cleanupSource = Join-Path $projectRoot 'installer\FoxMouse.Cleanup.cs'
    $cleanupPublishExe = Join-Path $cleanupPublish 'FoxMouse.Cleanup.exe'
    $cleanupAssemblyInfo = Join-Path $cleanupPublish 'FoxMouse.Cleanup.AssemblyInfo.cs'
    @(
        'using System.Reflection;'
        '[assembly: AssemblyTitle("FoxMouse Cleanup")]'
        '[assembly: AssemblyDescription("FoxMouse constrained maintenance cleanup worker")]'
        '[assembly: AssemblyCompany("FoxMouse Project")]'
        '[assembly: AssemblyProduct("FoxMouse")]'
        ('[assembly: AssemblyVersion("{0}.0")]' -f $version)
        ('[assembly: AssemblyFileVersion("{0}.0")]' -f $version)
        ('[assembly: AssemblyInformationalVersion("{0}")]' -f $version)
    ) | Set-Content -LiteralPath $cleanupAssemblyInfo -Encoding utf8
    $cleanupIcon = Join-Path $projectRoot 'assets\branding\generated\FoxMouse.ico'
    $cleanupManifest = Join-Path $projectRoot 'src\FoxMouse.Uninstall\app.manifest'
    & $frameworkCompiler `
        /nologo /target:winexe /platform:x64 /optimize+ `
        "/win32icon:$cleanupIcon" `
        "/win32manifest:$cleanupManifest" `
        "/out:$cleanupPublishExe" `
        $cleanupSource `
        $cleanupAssemblyInfo
    $cleanupCompileExitCode = $LASTEXITCODE
    if ($cleanupCompileExitCode -ne 0) {
        throw "FoxMouse.Cleanup compile failed with exit code $cleanupCompileExitCode."
    }

    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $appPublish -Label 'application publish root'
    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $guardPublish -Label 'Guard publish root'
    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $settingsPublish -Label 'Settings publish root'
    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $uninstallPublish -Label 'Uninstall publish root'
    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $cleanupPublish -Label 'Cleanup publish root'

    # Build-only material stays under the transaction root so the activated
    # release directory contains only publishable artifacts and manifests.
    $stageRoot = Join-Path $workRoot 'stage'
    $stageProduct = Join-Path $stageRoot 'FoxMouse'
    [void][IO.Directory]::CreateDirectory($stageProduct)
    foreach ($publishedItem in @(Get-ChildItem -LiteralPath $appPublish -Force)) {
        Copy-Item -LiteralPath $publishedItem.FullName -Destination $stageProduct -Recurse -Force -ErrorAction Stop
    }
    foreach ($publishedItem in @(Get-ChildItem -LiteralPath $guardPublish -Force)) {
        Copy-Item -LiteralPath $publishedItem.FullName -Destination $stageProduct -Recurse -Force -ErrorAction Stop
    }
    $stageSettings = Join-Path $stageProduct 'Settings'
    [void][IO.Directory]::CreateDirectory($stageSettings)
    foreach ($publishedItem in @(Get-ChildItem -LiteralPath $settingsPublish -Force)) {
        Copy-Item -LiteralPath $publishedItem.FullName -Destination $stageSettings -Recurse -Force -ErrorAction Stop
    }
    Copy-Item `
        -LiteralPath (Join-Path $uninstallPublish 'FoxMouse.Uninstall.exe') `
        -Destination $stageProduct `
        -Force `
        -ErrorAction Stop
    Copy-Item `
        -LiteralPath $cleanupPublishExe `
        -Destination $stageProduct `
        -Force `
        -ErrorAction Stop

    foreach ($releaseFile in @(
        (Join-Path $projectRoot 'README.md'),
        (Join-Path $projectRoot 'CHANGELOG.md'),
        (Join-Path $projectRoot 'LICENSE'),
        (Join-Path $projectRoot 'LICENSE-NOTICE.md'),
        (Join-Path $projectRoot 'docs\privacy.md'),
        (Join-Path $projectRoot 'docs\known-limitations.md'))) {
        Copy-Item -LiteralPath $releaseFile -Destination $stageProduct -Force -ErrorAction Stop
    }

    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $stageProduct -Label 'release payload'
    $stageApp = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $stageProduct 'FoxMouse.exe') `
        -ExpectedParent $stageProduct `
        -Label 'release FoxMouse.exe'
    $stageGuard = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $stageProduct 'FoxMouse.Guard.exe') `
        -ExpectedParent $stageProduct `
        -Label 'release FoxMouse.Guard.exe'
    $stageSettingsExe = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $stageSettings 'FoxMouse.Settings.exe') `
        -ExpectedParent $stageSettings `
        -Label 'release FoxMouse.Settings.exe'
    $stageUninstaller = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $stageProduct 'FoxMouse.Uninstall.exe') `
        -ExpectedParent $stageProduct `
        -Label 'release FoxMouse.Uninstall.exe'
    $stageCleanup = Assert-FoxMouseFile `
        -LiteralPath (Join-Path $stageProduct 'FoxMouse.Cleanup.exe') `
        -ExpectedParent $stageProduct `
        -Label 'release FoxMouse.Cleanup.exe'

    $signTool = $null
    if ($null -ne $normalizedThumbprint) {
        $sdkToolsRoot = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'
        $signTool = Get-ChildItem -LiteralPath $sdkToolsRoot `
            -Filter signtool.exe `
            -Recurse `
            -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Select-Object -First 1 -ExpandProperty FullName
        if ([string]::IsNullOrWhiteSpace($signTool)) {
            throw 'signtool.exe was not found.'
        }

        # Inner binaries are signed and verified before either container exists.
        Invoke-FoxMouseSign `
            -SignTool $signTool `
            -LiteralPath $stageApp `
            -SignerThumbprint $normalizedThumbprint
        Invoke-FoxMouseSign `
            -SignTool $signTool `
            -LiteralPath $stageGuard `
            -SignerThumbprint $normalizedThumbprint
        Invoke-FoxMouseSign `
            -SignTool $signTool `
            -LiteralPath $stageSettingsExe `
            -SignerThumbprint $normalizedThumbprint
        Invoke-FoxMouseSign `
            -SignTool $signTool `
            -LiteralPath $stageUninstaller `
            -SignerThumbprint $normalizedThumbprint
        Invoke-FoxMouseSign `
            -SignTool $signTool `
            -LiteralPath $stageCleanup `
            -SignerThumbprint $normalizedThumbprint
    }

    $portableName = "FoxMouse-v$version-win-x64.zip"
    $portablePath = Join-Path $candidateReleaseRoot $portableName
    Compress-Archive `
        -LiteralPath $stageProduct `
        -DestinationPath $portablePath `
        -CompressionLevel Optimal
    $null = Wait-FoxMouseFileReady -LiteralPath $portablePath

    $setupPath = Join-Path $candidateReleaseRoot 'FoxMouse-Setup-x64.exe'
    $setupPublish = Join-Path $publishRoot 'setup'
    & $dotnet publish (Join-Path $projectRoot 'src\FoxMouse.Setup\FoxMouse.Setup.csproj') `
        -c $Configuration -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:PublishTrimmed=false `
        "-p:FoxMousePackagePath=$portablePath" `
        -o $setupPublish --nologo
    $setupPublishExitCode = $LASTEXITCODE
    if ($setupPublishExitCode -ne 0) {
        throw "FoxMouse Setup publish failed with exit code $setupPublishExitCode."
    }
    Copy-Item `
        -LiteralPath (Join-Path $setupPublish 'FoxMouse-Setup-x64.exe') `
        -Destination $setupPath `
        -Force `
        -ErrorAction Stop
    $null = Wait-FoxMouseFileReady -LiteralPath $setupPath -TimeoutMilliseconds 30000

    if ($null -ne $signTool) {
        # The self-contained Setup executable is the final signed object; nothing mutates it afterward.
        Invoke-FoxMouseSign `
            -SignTool $signTool `
            -LiteralPath $setupPath `
            -SignerThumbprint $normalizedThumbprint
    }

    $hashes = foreach ($artifact in @($portablePath, $setupPath)) {
        $hash = Get-FileHash -LiteralPath $artifact -Algorithm SHA256
        [ordered]@{
            file = Split-Path -Leaf $artifact
            sha256 = $hash.Hash.ToLowerInvariant()
            bytes = (Get-Item -LiteralPath $artifact).Length
        }
    }
    $manifest = [ordered]@{
        schema = 'foxmouse.release/2'
        product = 'FoxMouse'
        version = $version
        runtime = 'win-x64'
        configuration = $Configuration
        signed = $null -ne $normalizedThumbprint
        signerThumbprint = $normalizedThumbprint
        installer = 'self-contained-dotnet'
        maintenance = @(
            'repair',
            'uninstall',
            'custom-install-parent',
            'arp-hkcu',
            'root-uninstaller',
            'quiet-maintenance-host',
            'detached-handoff',
            'constrained-cleanup-worker',
            'single-page-maintenance',
            'no-scroll-maintenance',
            'candidate-gated')
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        artifacts = @($hashes)
    }
    $manifest | ConvertTo-Json -Depth 4 |
        Set-Content -LiteralPath (Join-Path $candidateReleaseRoot 'release-manifest.json') -Encoding utf8
    $hashes | ForEach-Object { "$($_.sha256)  $($_.file)" } |
        Set-Content -LiteralPath (Join-Path $candidateReleaseRoot 'SHA256SUMS.txt') -Encoding ascii

    $null = Assert-FoxMouseTreeHasNoReparsePoints `
        -LiteralPath $candidateReleaseRoot `
        -Label 'candidate release root'
    $null = Assert-FoxMouseDirectoryRoot `
        -LiteralPath $artifactsRoot `
        -ExpectedParent $projectRoot `
        -Label 'artifacts root'
    $null = Assert-FoxMouseDirectoryRoot `
        -LiteralPath $releaseParent `
        -ExpectedParent $artifactsRoot `
        -Label 'release parent'

    # Run the full runtime/package verification against the transaction candidate.
    # The current release is left untouched unless every gate succeeds.
    & (Join-Path $scriptRoot 'Test-ReleaseArtifact.ps1') `
        -ReleaseRoot $candidateReleaseRoot `
        -ExpectedVersion $ExpectedVersion `
        -Candidate
    if (-not $?) {
        throw 'Candidate release artifact verification failed.'
    }

    if (Test-Path -LiteralPath $releaseRoot) {
        $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $releaseRoot -Label 'release root'
        Move-FoxMouseDirectory `
            -Source $releaseRoot `
            -SourceParent $releaseParent `
            -Destination $releaseBackup `
            -DestinationParent $releaseParent `
            -Label 'previous release backup'
        $oldReleaseBackedUp = $true
    }

    Move-FoxMouseDirectory `
        -Source $candidateReleaseRoot `
        -SourceParent $workRoot `
        -Destination $releaseRoot `
        -DestinationParent $releaseParent `
        -Label 'release activation'
    $newReleaseActivated = $true
    $committed = $true
    Assert-HistoricalReleaseSnapshot
}
catch {
    $originalFailure = $_.Exception.Message
    $rollbackFailures = New-Object 'System.Collections.Generic.List[string]'

    if ($newReleaseActivated) {
        if (-not (Test-Path -LiteralPath $releaseRoot)) {
            $rollbackFailures.Add("could not quarantine the failed release because the active release is missing: $releaseRoot")
        }
        elseif (Test-Path -LiteralPath $candidateReleaseRoot) {
            $rollbackFailures.Add("could not quarantine the failed release because the staging destination already exists: $candidateReleaseRoot")
        }
        else {
            try {
                Move-FoxMouseDirectory `
                    -Source $releaseRoot `
                    -SourceParent $releaseParent `
                    -Destination $candidateReleaseRoot `
                    -DestinationParent $workRoot `
                    -Label 'failed release rollback'
                $newReleaseActivated = $false
            }
            catch {
                $rollbackFailures.Add("could not quarantine the failed release: $($_.Exception.Message)")
            }
        }
    }

    if ($oldReleaseBackedUp) {
        if (-not (Test-Path -LiteralPath $releaseBackup)) {
            $rollbackFailures.Add("could not restore the previous release because its backup is missing: $releaseBackup")
        }
        elseif (Test-Path -LiteralPath $releaseRoot) {
            $rollbackFailures.Add("could not restore the previous release because the activation path is occupied: $releaseRoot")
        }
        else {
            try {
                Move-FoxMouseDirectory `
                    -Source $releaseBackup `
                    -SourceParent $releaseParent `
                    -Destination $releaseRoot `
                    -DestinationParent $releaseParent `
                    -Label 'previous release rollback'
                $oldReleaseBackedUp = $false
            }
            catch {
                $rollbackFailures.Add("could not restore the previous release: $($_.Exception.Message)")
            }
        }
    }

    $message = "FoxMouse release creation failed: $originalFailure"
    if ($rollbackFailures.Count -gt 0) {
        $message += ' Rollback also reported: ' + ($rollbackFailures -join '; ')
    }
    throw $message
}
finally {
    if ($committed -and (Test-Path -LiteralPath $releaseBackup)) {
        try {
            Remove-FoxMouseTree `
                -LiteralPath $releaseBackup `
                -ExpectedParent $releaseParent `
                -Label 'committed previous release backup'
        }
        catch {
            Write-Warning "The previous release backup was retained: $($_.Exception.Message)"
        }
    }

    if (Test-Path -LiteralPath $workRoot) {
        try {
            Remove-FoxMouseTree `
                -LiteralPath $workRoot `
                -ExpectedParent $artifactsRoot `
                -Label 'release staging root'
        }
        catch {
            Write-Warning "The release staging root was retained: $($_.Exception.Message)"
        }
    }
}

Write-Output "Release created: $releaseRoot"
