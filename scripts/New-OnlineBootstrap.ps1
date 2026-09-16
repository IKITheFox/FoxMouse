[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SetupDirectory,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$taskProjectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
. (Join-Path $PSScriptRoot 'FoxMouse.ScriptSafety.ps1')
$taskSetupRoot = Assert-FoxMouseDirectoryRoot -LiteralPath $SetupDirectory -Label 'framework-dependent setup directory'
$null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $taskSetupRoot -Label 'setup directory'
$taskOutput = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $taskOutput) { throw 'Output already exists; choose a new output path.' }
$taskOutputParent = Assert-FoxMouseDirectoryRoot -LiteralPath (Split-Path -Parent $taskOutput) -Label 'output parent'
$null = Assert-FoxMouseFile -LiteralPath (Join-Path $taskSetupRoot 'FoxMouse-Setup-x64.exe') -ExpectedParent $taskSetupRoot -Label 'setup executable'
$null = Assert-FoxMouseFile -LiteralPath (Join-Path $taskSetupRoot 'FoxMouse-package.zip') -ExpectedParent $taskSetupRoot -Label 'FoxMouse application payload'
$taskRuntimeConfig = Get-Content -LiteralPath (Join-Path $taskSetupRoot 'FoxMouse-Setup-x64.runtimeconfig.json') -Raw | ConvertFrom-Json
if (!$taskRuntimeConfig.runtimeOptions.PSObject.Properties['framework'] -and
    !$taskRuntimeConfig.runtimeOptions.PSObject.Properties['frameworks']) {
    throw 'Online setup must be published framework-dependent.'
}
if (Test-Path -LiteralPath (Join-Path $taskSetupRoot 'coreclr.dll')) { throw 'Shared runtime files must not be included in online setup.' }
$taskWork = Join-Path (Join-Path $taskProjectRoot 'artifacts') ('.online-bootstrap-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($taskWork)
$taskBundle = Join-Path $taskWork 'SetupBundle.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($taskSetupRoot, $taskBundle, [IO.Compression.CompressionLevel]::Optimal, $false)
$taskCompiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$taskCandidate = Join-Path $taskWork 'FoxMouse-OnlineSetup-x64.exe'
$taskVersionSource = Join-Path $taskWork 'AssemblyInfo.cs'
& (Join-Path $PSScriptRoot 'New-NativeVersionSource.ps1') -Component 'Online Setup' -OutputPath $taskVersionSource
$taskSetupVersion = (Get-Item -LiteralPath (Join-Path $taskSetupRoot 'FoxMouse-Setup-x64.exe')).VersionInfo.FileVersion
[xml]$taskVersionProperties = Get-Content -LiteralPath (Join-Path $taskProjectRoot 'Directory.Build.props') -Raw
if ($taskSetupVersion -ne [string]$taskVersionProperties.Project.PropertyGroup.FileVersion) {
    throw "Setup version $taskSetupVersion does not match the current build version."
}
$taskSources = @('BootstrapDependencyPolicy.cs','BootstrapDownloader.cs','BootstrapInventory.cs','BootstrapBundle.cs','ThemeAwareComboBox.cs','BootstrapProgram.cs') |
    ForEach-Object { Join-Path (Join-Path $taskProjectRoot 'installer') $_ }
$taskSources += $taskVersionSource
$taskCompilerOutput = & $taskCompiler /nologo /target:winexe /platform:x64 /optimize+ `
    /r:System.Net.Http.dll /r:System.IO.Compression.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll `
    "/win32manifest:$(Join-Path $taskProjectRoot 'src/FoxMouse.Setup/app.manifest')" `
    "/win32icon:$(Join-Path $taskProjectRoot 'assets/branding/generated/FoxMouse.ico')" `
    "/resource:$taskBundle,FoxMouse.SetupBundle.zip" "/out:$taskCandidate" @taskSources
if ($LASTEXITCODE -ne 0) { throw "Bootstrap compilation failed: $LASTEXITCODE`n$($taskCompilerOutput -join [Environment]::NewLine)" }
# No old output is overwritten; leave build inputs as evidence until release cleanup.
[IO.File]::Copy($taskCandidate, $taskOutput, $false)
[pscustomobject]@{
    Path = $taskOutput
    Bytes = (Get-Item -LiteralPath $taskOutput).Length
    SHA256 = (Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256).Hash
    BuildEvidence = $taskWork
}
