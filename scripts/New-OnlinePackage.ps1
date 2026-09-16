[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FoxMouse.ScriptSafety.ps1')
. (Join-Path $PSScriptRoot 'Get-DotNet.ps1')
$taskProject = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$taskArtifacts = Assert-FoxMouseDirectoryRoot -LiteralPath (Join-Path $taskProject 'artifacts') -Label 'artifacts root'
$null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $PSScriptRoot -Label 'build scripts'
$taskDotNet = Get-FoxMouseDotNet
# A unique candidate directory: never overwrite a previous release or build.
$taskRoot = Join-Path $taskArtifacts ('online-candidate-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($taskRoot)
$taskProduct = Join-Path $taskRoot 'payload/FoxMouse'
[void][IO.Directory]::CreateDirectory($taskProduct)

foreach ($taskName in @('App', 'Guard', 'Settings', 'Uninstall', 'Setup')) {
    $taskPublish = Join-Path $taskRoot ('publish/' + $taskName)
    $taskSingle = if ($taskName -eq 'Uninstall') { 'true' } else { 'false' }
    & $taskDotNet publish (Join-Path $taskProject "src/FoxMouse.$taskName/FoxMouse.$taskName.csproj") `
        -c $Configuration -r win-x64 --self-contained false `
        "-p:PublishSingleFile=$taskSingle" -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=false `
        -p:WindowsAppSDKSelfContained=false -o $taskPublish --nologo
    if ($LASTEXITCODE -ne 0) { throw "$taskName publish failed: $LASTEXITCODE" }
    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $taskPublish -Label "$taskName publish"
    if (Test-Path -LiteralPath (Join-Path $taskPublish 'coreclr.dll')) {
        throw "$taskName unexpectedly contains a shared runtime."
    }
    if ($taskName -eq 'Setup') { continue }
    if ($taskName -eq 'Uninstall') {
        # Maintenance is copied outside the installation directory during uninstall;
        # keep its managed code and localized resources in one executable.
        Copy-Item -LiteralPath (Join-Path $taskPublish 'FoxMouse.Uninstall.exe') -Destination $taskProduct
        continue
    }
    $taskDestination = if ($taskName -eq 'Settings') { Join-Path $taskProduct 'Settings' } else { $taskProduct }
    [void][IO.Directory]::CreateDirectory($taskDestination)
    foreach ($taskItem in Get-ChildItem -LiteralPath $taskPublish -Force) {
        Copy-Item -LiteralPath $taskItem.FullName -Destination $taskDestination -Recurse -Force
    }
}

$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$taskCleanupVersionSource = Join-Path $taskRoot 'Cleanup.AssemblyInfo.cs'
& (Join-Path $PSScriptRoot 'New-NativeVersionSource.ps1') -Component 'Cleanup' -OutputPath $taskCleanupVersionSource
& $taskCompiler /nologo /target:winexe /platform:x64 /optimize+ `
    "/win32manifest:$(Join-Path $taskProject 'src/FoxMouse.Uninstall/app.manifest')" `
    "/win32icon:$(Join-Path $taskProject 'assets/branding/generated/FoxMouse.ico')" `
    "/out:$(Join-Path $taskProduct 'FoxMouse.Cleanup.exe')" `
    (Join-Path $taskProject 'installer/FoxMouse.Cleanup.cs') $taskCleanupVersionSource
if ($LASTEXITCODE -ne 0) { throw "Cleanup compilation failed: $LASTEXITCODE" }
foreach ($taskDocument in @('README.md', 'CHANGELOG.md', 'LICENSE', 'LICENSE-NOTICE.md', 'docs/privacy.md', 'docs/known-limitations.md')) {
    Copy-Item -LiteralPath (Join-Path $taskProject $taskDocument) -Destination $taskProduct
}
$taskSetup = Join-Path $taskRoot 'publish/Setup'
Compress-Archive -LiteralPath $taskProduct -DestinationPath (Join-Path $taskSetup 'FoxMouse-package.zip') -CompressionLevel Optimal
$taskResult = & (Join-Path $PSScriptRoot 'New-OnlineBootstrap.ps1') `
    -SetupDirectory $taskSetup -OutputPath (Join-Path $taskRoot 'FoxMouse-OnlineSetup-x64.exe')
$taskResult | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRoot 'candidate.json') -Encoding utf8
[xml]$taskVersionProperties = Get-Content -LiteralPath (Join-Path $taskProject 'Directory.Build.props') -Raw
$null = & (Join-Path $PSScriptRoot 'Test-OnlineArtifact.ps1') -CandidateDirectory $taskRoot `
    -ExpectedVersion ([string]$taskVersionProperties.Project.PropertyGroup.Version)
$taskResult
