function Get-FoxMouseDotNet {
    [CmdletBinding()]
    param()

    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $programFilesPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $candidate = Join-Path $programFilesPath 'dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $candidate) {
        return $candidate
    }

    throw 'A .NET 10 SDK is required. dotnet.exe was not found.'
}
