[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Online Setup', 'Cleanup')][string]$Component,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$taskProject = Split-Path -Parent $PSScriptRoot
[xml]$taskProperties = Get-Content -LiteralPath (Join-Path $taskProject 'Directory.Build.props') -Raw
$taskVersion = [string]$taskProperties.Project.PropertyGroup.Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid product version.' }
$taskInformationalVersion = [string]$taskProperties.Project.PropertyGroup.InformationalVersion
if ([string]::IsNullOrWhiteSpace($taskInformationalVersion)) { $taskInformationalVersion = $taskVersion }
if ($taskInformationalVersion -notmatch '^\d+\.\d+\.\d+(-[A-Za-z0-9.-]+)?$') { throw 'Invalid informational version.' }
if (Test-Path -LiteralPath $OutputPath) { throw 'Version source already exists.' }
# Generated build input for the Windows inbox C# compiler, not a second version source.
@(
    'using System.Reflection;'
    ('[assembly: AssemblyTitle("FoxMouse {0}")]' -f $Component)
    '[assembly: AssemblyCompany("FoxMouse Project")]'
    '[assembly: AssemblyProduct("FoxMouse")]'
    ('[assembly: AssemblyVersion("{0}.0")]' -f $taskVersion)
    ('[assembly: AssemblyFileVersion("{0}.0")]' -f $taskVersion)
    ('[assembly: AssemblyInformationalVersion("{0}")]' -f $taskInformationalVersion)
) | Set-Content -LiteralPath $OutputPath -Encoding utf8
