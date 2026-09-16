[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'FoxMouse.ScriptSafety.ps1')
$checkRoot = Assert-FoxMouseDirectoryRoot -LiteralPath $CandidateDirectory -Label 'online candidate'
$null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $checkRoot -Label 'online candidate'
function Get-CheckedFile([string]$RelativePath) {
    Assert-FoxMouseFile -LiteralPath (Join-Path $checkRoot $RelativePath) -ExpectedParent $checkRoot -Label $RelativePath
}
$checkManifest = Get-Content -LiteralPath (Get-CheckedFile 'candidate.json') -Raw | ConvertFrom-Json
$checkInstaller = Get-CheckedFile 'FoxMouse-OnlineSetup-x64.exe'
if ([IO.Path]::GetFullPath($checkManifest.Path) -ne $checkInstaller -or
    (Get-Item -LiteralPath $checkInstaller).Length -ne $checkManifest.Bytes -or
    (Get-FileHash -LiteralPath $checkInstaller -Algorithm SHA256).Hash -ne $checkManifest.SHA256) {
    throw 'Online installer identity, size or SHA256 mismatch.'
}
foreach ($checkBinary in @('FoxMouse-OnlineSetup-x64.exe',
    'payload/FoxMouse/FoxMouse.exe', 'payload/FoxMouse/FoxMouse.Guard.exe',
    'payload/FoxMouse/FoxMouse.Uninstall.exe', 'payload/FoxMouse/FoxMouse.Cleanup.exe',
    'payload/FoxMouse/Settings/FoxMouse.Settings.exe', 'publish/Setup/FoxMouse-Setup-x64.exe')) {
    if ((Get-Item -LiteralPath (Get-CheckedFile $checkBinary)).VersionInfo.FileVersion -ne "$ExpectedVersion.0") {
        throw "Version mismatch: $checkBinary"
    }
}
foreach ($checkRequired in @('payload/FoxMouse/en-US/FoxMouse.Core.resources.dll',
    'payload/FoxMouse/Settings/en-US/FoxMouse.Core.resources.dll',
    'payload/FoxMouse/Settings/FoxMouse.Settings.pri', 'publish/Setup/en-US/FoxMouse.Core.resources.dll')) {
    $null = Get-CheckedFile $checkRequired
}
foreach ($checkFolder in @('payload/FoxMouse', 'publish/Setup')) {
    $checkFiles = Get-ChildItem -LiteralPath (Join-Path $checkRoot $checkFolder) -File -Recurse
    if ($checkFiles | Where-Object Name -In @('coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll')) {
        throw "Shared runtime found in framework-dependent payload: $checkFolder"
    }
    foreach ($checkConfigFile in $checkFiles | Where-Object Name -Like '*.runtimeconfig.json') {
        $checkOptions = (Get-Content -LiteralPath $checkConfigFile.FullName -Raw | ConvertFrom-Json).runtimeOptions
        $checkFrameworks = @()
        if ($checkOptions.PSObject.Properties['frameworks']) { $checkFrameworks += @($checkOptions.frameworks) }
        if ($checkOptions.PSObject.Properties['framework']) { $checkFrameworks += @($checkOptions.framework) }
        if ($checkOptions.PSObject.Properties['includedFrameworks'] -or !$checkFrameworks.Count) { throw "Not framework-dependent: $($checkConfigFile.Name)" }
        foreach ($checkFramework in $checkFrameworks) {
            $checkVersion = [Version]$checkFramework.version
            if ($checkFramework.name -notin @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') -or
                $checkVersion.Major -ne 10 -or $checkVersion.Minor -ne 0 -or $checkVersion -gt [Version]'10.0.11') {
                throw "Bootstrap prerequisite cannot satisfy $($checkConfigFile.Name)"
            }
        }
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$checkZip = [IO.Compression.ZipFile]::OpenRead((Get-CheckedFile 'publish/Setup/FoxMouse-package.zip'))
try {
    $checkNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($checkEntry in $checkZip.Entries) {
        $checkName = $checkEntry.FullName.Replace('\', '/')
        if (!$checkNames.Add($checkName) -or !$checkName.StartsWith('FoxMouse/') -or
            $checkName.Contains(':') -or ($checkName.Split('/') -contains '..')) { throw "Unsafe or duplicate ZIP entry: $checkName" }
        if ($checkName.EndsWith('/')) { continue }
        $checkFile = Get-CheckedFile ('payload/' + $checkName)
        if ((Get-Item -LiteralPath $checkFile).Length -ne $checkEntry.Length) { throw "ZIP size mismatch: $checkName" }
        $checkStream = $checkEntry.Open()
        $checkHash = [Security.Cryptography.SHA256]::Create()
        try { $checkDigest = [BitConverter]::ToString($checkHash.ComputeHash($checkStream)).Replace('-', '') }
        finally { $checkHash.Dispose(); $checkStream.Dispose() }
        if ($checkDigest -ne (Get-FileHash -LiteralPath $checkFile -Algorithm SHA256).Hash) { throw "ZIP content mismatch: $checkName" }
    }
    foreach ($checkFile in Get-ChildItem -LiteralPath (Join-Path $checkRoot 'payload/FoxMouse') -File -Recurse) {
        $checkRelative = [IO.Path]::GetRelativePath((Join-Path $checkRoot 'payload'), $checkFile.FullName).Replace('\', '/')
        if (!$checkNames.Contains($checkRelative)) { throw "Payload omitted from ZIP: $checkRelative" }
    }
} finally { $checkZip.Dispose() }
[pscustomobject]@{ Result='Passed'; Version=$ExpectedVersion; Bytes=$checkManifest.Bytes; SHA256=$checkManifest.SHA256; Scope='Static artifact integrity; not clean-machine installation acceptance' }
