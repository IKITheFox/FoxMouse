Set-StrictMode -Version 2.0

if ($null -eq ('FoxMouse.ScriptSafety.NativeMethods' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace FoxMouse.ScriptSafety
{
    public static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostThreadMessage(
            uint threadId,
            uint message,
            UIntPtr wParam,
            IntPtr lParam);
    }
}
'@
}

function Get-FoxMouseFullPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    $fullPath = [IO.Path]::GetFullPath($LiteralPath)
    if ($fullPath.Length -gt 3) {
        $fullPath = $fullPath.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    }

    return $fullPath
}

function Test-FoxMousePathEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Left,
        [Parameter(Mandatory)]
        [string]$Right
    )

    return [string]::Equals(
        (Get-FoxMouseFullPath -LiteralPath $Left),
        (Get-FoxMouseFullPath -LiteralPath $Right),
        [StringComparison]::OrdinalIgnoreCase)
}

function Assert-FoxMouseChildPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [Parameter(Mandatory)]
        [string]$ExpectedParent,
        [string]$Label = 'path'
    )

    $fullPath = Get-FoxMouseFullPath -LiteralPath $LiteralPath
    $fullParent = Get-FoxMouseFullPath -LiteralPath $ExpectedParent
    $prefix = $fullParent + [IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe $Label '$fullPath': it is not a child of '$fullParent'."
    }

    return $fullPath
}

function Test-FoxMouseIsReparsePoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath
    )

    $item = Get-Item -LiteralPath $LiteralPath -Force -ErrorAction Stop
    return [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Assert-FoxMouseDirectoryRoot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [string]$ExpectedParent,
        [string]$Label = 'directory',
        [switch]$AllowMissing
    )

    $fullPath = Get-FoxMouseFullPath -LiteralPath $LiteralPath
    if (-not [string]::IsNullOrWhiteSpace($ExpectedParent)) {
        $fullPath = Assert-FoxMouseChildPath -LiteralPath $fullPath -ExpectedParent $ExpectedParent -Label $Label
    }

    if (-not (Test-Path -LiteralPath $fullPath)) {
        if ($AllowMissing) {
            return $fullPath
        }

        throw "$Label does not exist: $fullPath"
    }

    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if (-not $item.PSIsContainer) {
        throw "$label is not a directory: $fullPath"
    }

    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Unsafe $Label '$fullPath': reparse points and junctions are not allowed."
    }

    return $fullPath
}

function Assert-FoxMouseFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [string]$ExpectedParent,
        [string]$Label = 'file'
    )

    $fullPath = Get-FoxMouseFullPath -LiteralPath $LiteralPath
    if (-not [string]::IsNullOrWhiteSpace($ExpectedParent)) {
        $fullPath = Assert-FoxMouseChildPath -LiteralPath $fullPath -ExpectedParent $ExpectedParent -Label $Label
    }

    $item = Get-Item -LiteralPath $fullPath -Force -ErrorAction Stop
    if ($item.PSIsContainer) {
        throw "$Label is not a file: $fullPath"
    }

    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Unsafe $Label '$fullPath': reparse points are not allowed."
    }

    return $fullPath
}

function Assert-FoxMouseTreeHasNoReparsePoints {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [string]$Label = 'tree'
    )

    $fullPath = Assert-FoxMouseDirectoryRoot -LiteralPath $LiteralPath -Label $Label
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($fullPath)
    while ($pending.Count -gt 0) {
        $current = $pending.Pop()
        foreach ($item in @(Get-ChildItem -LiteralPath $current -Force -ErrorAction Stop)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Unsafe $Label '$fullPath': it contains reparse point '$($item.FullName)'."
            }

            if ($item.PSIsContainer) {
                $pending.Push($item.FullName)
            }
        }
    }

    return $fullPath
}

function Remove-FoxMouseTree {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [Parameter(Mandatory)]
        [string]$ExpectedParent,
        [string]$Label = 'tree'
    )

    $null = Assert-FoxMouseDirectoryRoot -LiteralPath $ExpectedParent -Label "$Label parent"
    $fullPath = Assert-FoxMouseChildPath -LiteralPath $LiteralPath -ExpectedParent $ExpectedParent -Label $Label
    if (-not (Test-Path -LiteralPath $fullPath)) {
        return
    }

    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $fullPath -Label $Label
    Remove-Item -LiteralPath $fullPath -Recurse -Force -ErrorAction Stop
}

function Move-FoxMouseDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Source,
        [Parameter(Mandatory)]
        [string]$SourceParent,
        [Parameter(Mandatory)]
        [string]$Destination,
        [Parameter(Mandatory)]
        [string]$DestinationParent,
        [string]$Label = 'directory'
    )

    $null = Assert-FoxMouseDirectoryRoot -LiteralPath $SourceParent -Label "$Label source parent"
    $null = Assert-FoxMouseDirectoryRoot -LiteralPath $DestinationParent -Label "$Label destination parent"
    $fullSource = Assert-FoxMouseChildPath -LiteralPath $Source -ExpectedParent $SourceParent -Label "$Label source"
    $fullDestination = Assert-FoxMouseChildPath -LiteralPath $Destination -ExpectedParent $DestinationParent -Label "$Label destination"
    $null = Assert-FoxMouseTreeHasNoReparsePoints -LiteralPath $fullSource -Label "$Label source"
    if (Test-Path -LiteralPath $fullDestination) {
        throw "Unsafe $Label move: destination already exists: $fullDestination"
    }

    [IO.Directory]::Move($fullSource, $fullDestination)
}

function Normalize-FoxMouseThumbprint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Thumbprint
    )

    $normalized = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($normalized -notmatch '^[0-9A-F]{40}$') {
        throw 'A certificate thumbprint must contain exactly 40 hexadecimal characters.'
    }

    return $normalized
}

function Assert-FoxMouseAuthenticodeSignature {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [Parameter(Mandatory)]
        [string]$ExpectedSignerThumbprint
    )

    $fullPath = Assert-FoxMouseFile -LiteralPath $LiteralPath -Label 'signed payload'
    $expected = Normalize-FoxMouseThumbprint -Thumbprint $ExpectedSignerThumbprint
    $signature = Get-AuthenticodeSignature -LiteralPath $fullPath -ErrorAction Stop
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate) {
        throw "Authenticode signature validation failed for '$fullPath': $($signature.StatusMessage)"
    }

    $actual = Normalize-FoxMouseThumbprint -Thumbprint $signature.SignerCertificate.Thumbprint
    if (-not [string]::Equals($actual, $expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected signer for '$fullPath'. Expected '$expected'; found '$actual'."
    }
}

function Assert-FoxMouseZipEntriesSafe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ArchivePath,
        [Parameter(Mandatory)]
        [string]$DestinationRoot,
        [int]$MaximumEntryCount = 10000,
        [long]$MaximumExpandedBytes = 2147483648
    )

    $archive = Assert-FoxMouseFile -LiteralPath $ArchivePath -Label 'ZIP package'
    $destination = Get-FoxMouseFullPath -LiteralPath $DestinationRoot
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction Stop
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        if ($zip.Entries.Count -eq 0 -or $zip.Entries.Count -gt $MaximumEntryCount) {
            throw "ZIP package entry count is outside the permitted range: $($zip.Entries.Count)"
        }

        $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        [long]$expandedBytes = 0
        foreach ($entry in $zip.Entries) {
            $entryName = $entry.FullName.Replace('/', [IO.Path]::DirectorySeparatorChar)
            if ([string]::IsNullOrWhiteSpace($entryName) -or
                [IO.Path]::IsPathRooted($entryName) -or
                $entryName.Contains(':')) {
                throw "ZIP package contains an unsafe entry name: '$($entry.FullName)'"
            }

            $segments = $entryName.Split(@([IO.Path]::DirectorySeparatorChar), [StringSplitOptions]::RemoveEmptyEntries)
            if ($segments -contains '..' -or $segments -contains '.') {
                throw "ZIP package contains a traversal entry: '$($entry.FullName)'"
            }

            $entryDestination = Get-FoxMouseFullPath -LiteralPath (Join-Path $destination $entryName)
            $null = Assert-FoxMouseChildPath `
                -LiteralPath $entryDestination `
                -ExpectedParent $destination `
                -Label 'ZIP entry destination'
            if (-not $seen.Add($entryDestination)) {
                throw "ZIP package contains duplicate destination '$entryDestination'."
            }

            $unixFileType = ([uint32]$entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixFileType -eq 0xA000) {
                throw "ZIP package contains symbolic link '$($entry.FullName)'."
            }

            $expandedBytes += [long]$entry.Length
            if ($expandedBytes -gt $MaximumExpandedBytes) {
                throw "ZIP package expands beyond the permitted $MaximumExpandedBytes-byte limit."
            }
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Get-FoxMouseProductProcesses {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$InstallRoot
    )

    $fullInstallRoot = Get-FoxMouseFullPath -LiteralPath $InstallRoot
    $currentSessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $expected = @{
        'FoxMouse' = Get-FoxMouseFullPath -LiteralPath (Join-Path $fullInstallRoot 'FoxMouse.exe')
        'FoxMouse.Guard' = Get-FoxMouseFullPath -LiteralPath (Join-Path $fullInstallRoot 'FoxMouse.Guard.exe')
        'FoxMouse.Settings' = Get-FoxMouseFullPath -LiteralPath (Join-Path $fullInstallRoot 'Settings\FoxMouse.Settings.exe')
    }

    foreach ($name in @('FoxMouse.Settings', 'FoxMouse', 'FoxMouse.Guard')) {
        foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
            try {
                if ($process.HasExited -or $process.SessionId -ne $currentSessionId) {
                    $process.Dispose()
                    continue
                }

                $imagePath = Get-FoxMouseFullPath -LiteralPath $process.Path
                if (-not [string]::Equals(
                    $imagePath,
                    $expected[$name],
                    [StringComparison]::OrdinalIgnoreCase)) {
                    $process.Dispose()
                    continue
                }

                [PSCustomObject]@{
                    Process = $process
                    ImagePath = $imagePath
                    Role = $name
                    SessionId = $currentSessionId
                }
            }
            catch {
                # A process whose image path cannot be proven is never acted on.
                $process.Dispose()
            }
        }
    }
}

function Test-FoxMouseProcessIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [Diagnostics.Process]$Process,
        [Parameter(Mandatory)]
        [string]$ExpectedImagePath,
        [Parameter(Mandatory)]
        [int]$ExpectedSessionId
    )

    try {
        if ($Process.HasExited -or $Process.SessionId -ne $ExpectedSessionId) {
            return $false
        }

        $actualPath = Get-FoxMouseFullPath -LiteralPath $Process.Path
        return [string]::Equals(
            $actualPath,
            (Get-FoxMouseFullPath -LiteralPath $ExpectedImagePath),
            [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

function Invoke-FoxMouseCursorRestore {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$RecoveryExecutable,
        [int]$TimeoutMilliseconds = 10000
    )

    $recoveryPath = Assert-FoxMouseFile -LiteralPath $RecoveryExecutable -Label 'cursor recovery executable'
    $process = Start-Process -FilePath $recoveryPath -ArgumentList '--restore-cursor' -PassThru -WindowStyle Hidden
    try {
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            throw "Cursor recovery timed out after $TimeoutMilliseconds ms."
        }

        if ($process.ExitCode -ne 0) {
            throw "Cursor recovery failed with exit code $($process.ExitCode)."
        }
    }
    finally {
        $process.Dispose()
    }
}

function Stop-FoxMouseProductProcessesSafely {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$InstallRoot,
        [Parameter(Mandatory)]
        [string]$RecoveryExecutable,
        [int]$CooperativeTimeoutMilliseconds = 5000,
        [int]$ForcedTimeoutMilliseconds = 5000
    )

    $running = @(Get-FoxMouseProductProcesses -InstallRoot $InstallRoot)
    if ($running.Count -eq 0) {
        return
    }

    # Never ask or force a running product process to exit until a separate
    # Guard invocation has positively confirmed that the system cursor is shown.
    Invoke-FoxMouseCursorRestore -RecoveryExecutable $RecoveryExecutable

    $shutdownOrder = @{
        'FoxMouse.Settings' = 0
        'FoxMouse' = 1
        'FoxMouse.Guard' = 2
    }
    foreach ($entry in @($running | Sort-Object @{ Expression = { $shutdownOrder[$_.Role] } })) {
        if (Test-FoxMouseProcessIdentity `
            -Process $entry.Process `
            -ExpectedImagePath $entry.ImagePath `
            -ExpectedSessionId $entry.SessionId) {
            try {
                $null = $entry.Process.CloseMainWindow()
            }
            catch {
                # A process without a closeable top-level window is handled by
                # the bounded cooperative wait below.
            }

            if ($entry.Role -eq 'FoxMouse') {
                # FoxMouse is a tray ApplicationContext and usually has no main
                # window. WM_QUIT lets its WinForms message loop return through
                # Program.finally, which disposes the engine and Guard cleanly.
                try {
                    foreach ($thread in @($entry.Process.Threads)) {
                        try {
                            $null = [FoxMouse.ScriptSafety.NativeMethods]::PostThreadMessage(
                                [uint32]$thread.Id,
                                [uint32]0x0012,
                                [UIntPtr]::Zero,
                                [IntPtr]::Zero)
                        }
                        finally {
                            $thread.Dispose()
                        }
                    }
                }
                catch {
                    # The process can exit while its threads are enumerated.
                }
            }
        }
    }

    $deadline = [DateTime]::UtcNow.AddMilliseconds($CooperativeTimeoutMilliseconds)
    do {
        Start-Sleep -Milliseconds 100
        $remaining = @(Get-FoxMouseProductProcesses -InstallRoot $InstallRoot)
    } while ($remaining.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline)

    if ($remaining.Count -gt 0) {
        # This is intentionally a new recovery process, immediately before the
        # force phase. A failed Show confirmation aborts without killing.
        Invoke-FoxMouseCursorRestore -RecoveryExecutable $RecoveryExecutable

        $forceFailure = $null
        try {
            $ordered = @($remaining | Sort-Object @{ Expression = { $shutdownOrder[$_.Role] } })
            foreach ($entry in $ordered) {
                if (Test-FoxMouseProcessIdentity `
                    -Process $entry.Process `
                    -ExpectedImagePath $entry.ImagePath `
                    -ExpectedSessionId $entry.SessionId) {
                    try {
                        $entry.Process.Kill()
                    }
                    catch {
                        if (Test-FoxMouseProcessIdentity `
                            -Process $entry.Process `
                            -ExpectedImagePath $entry.ImagePath `
                            -ExpectedSessionId $entry.SessionId) {
                            throw
                        }
                    }
                }
            }
        }
        catch {
            $forceFailure = $_
        }

        $forcedDeadline = [DateTime]::UtcNow.AddMilliseconds($ForcedTimeoutMilliseconds)
        do {
            Start-Sleep -Milliseconds 100
            $remaining = @(Get-FoxMouseProductProcesses -InstallRoot $InstallRoot)
        } while ($remaining.Count -gt 0 -and [DateTime]::UtcNow -lt $forcedDeadline)

        # Confirm visibility once more after terminating the owner and Guard.
        Invoke-FoxMouseCursorRestore -RecoveryExecutable $RecoveryExecutable
        if ($null -ne $forceFailure) {
            throw "Failed to terminate a verified FoxMouse process: $($forceFailure.Exception.Message)"
        }
    }

    $remaining = @(Get-FoxMouseProductProcesses -InstallRoot $InstallRoot)
    if ($remaining.Count -gt 0) {
        $identities = ($remaining | ForEach-Object { "$($_.ImagePath) (PID $($_.Process.Id))" }) -join ', '
        throw "FoxMouse processes did not exit: $identities"
    }
}

function Wait-FoxMouseFileReady {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [int]$TimeoutMilliseconds = 10000
    )

    $fullPath = Get-FoxMouseFullPath -LiteralPath $LiteralPath
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        if (Test-Path -LiteralPath $fullPath -PathType Leaf) {
            try {
                $stream = [IO.File]::Open($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                try {
                    if ($stream.Length -gt 0) {
                        return $fullPath
                    }
                }
                finally {
                    $stream.Dispose()
                }
            }
            catch [IO.IOException] {
                # The producer can still have the file open; retry until timeout.
            }
        }

        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "File was not produced completely within $TimeoutMilliseconds ms: $fullPath"
}

function ConvertTo-FoxMouseWindowsCommandLineArgument {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Argument
    )

    # ProcessStartInfo.ArgumentList is unavailable in Windows PowerShell 5.1.
    # Build a CreateProcess-compatible command line using the documented
    # CommandLineToArgvW backslash/quote rules so paths containing spaces remain
    # a single argument on both Windows PowerShell and modern PowerShell.
    if ($Argument.Length -gt 0 -and $Argument -notmatch '[\s"]') {
        return $Argument
    }

    $builder = New-Object Text.StringBuilder
    [void]$builder.Append('"')
    $backslashCount = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char]'\') {
            $backslashCount++
            continue
        }

        if ($character -eq [char]'"') {
            if ($backslashCount -gt 0) {
                [void]$builder.Append(('\' * ($backslashCount * 2)))
            }
            [void]$builder.Append('\"')
        }
        else {
            if ($backslashCount -gt 0) {
                [void]$builder.Append(('\' * $backslashCount))
            }
            [void]$builder.Append($character)
        }
        $backslashCount = 0
    }

    if ($backslashCount -gt 0) {
        [void]$builder.Append(('\' * ($backslashCount * 2)))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function New-FoxMouseProcessStartInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Executable,
        [AllowEmptyCollection()]
        [string[]]$ArgumentList = @(),
        [switch]$Hidden
    )

    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = $Executable
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Arguments = (@(
        foreach ($argument in $ArgumentList) {
            if ($null -eq $argument) {
                throw 'Process arguments cannot contain null values.'
            }
            ConvertTo-FoxMouseWindowsCommandLineArgument -Argument $argument
        }
    ) -join ' ')
    if ($Hidden) {
        $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    }
    return $startInfo
}
