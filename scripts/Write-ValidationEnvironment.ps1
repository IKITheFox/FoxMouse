[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = Split-Path -Parent $resolvedOutput
if ([string]::IsNullOrWhiteSpace($outputDirectory)) {
    throw 'OutputPath must have a parent directory.'
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
Add-Type -AssemblyName System.Windows.Forms
# Windows PowerShell 5.1 exposes the desktop drawing types through the
# System.Drawing assembly name. PowerShell 7/.NET resolves the same types after
# this load, while the NuGet-only System.Drawing.Common name is not available
# to the Windows PowerShell host used by the M5 gate.
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class FoxMouseValidationDpi
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitor,
        int dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("shcore.dll")]
    private static extern int GetScaleFactorForMonitor(
        IntPtr monitor,
        out int scalePercent);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    public static uint GetDpiAt(int x, int y)
    {
        IntPtr monitor = MonitorFromPoint(new Point { X = x, Y = y }, 2);
        if (monitor != IntPtr.Zero)
        {
            // Unlike GetDpiForMonitor, this API is not virtualized to 96 DPI
            // merely because the PowerShell validation host is DPI-unaware.
            int scalePercent;
            if (GetScaleFactorForMonitor(monitor, out scalePercent) == 0 &&
                scalePercent >= 50 && scalePercent <= 800)
            {
                return (uint)Math.Round(scalePercent * 96d / 100d);
            }

            uint dpiX;
            uint dpiY;
            if (GetDpiForMonitor(monitor, 0, out dpiX, out dpiY) == 0 &&
                dpiX >= 48 && dpiX <= 768)
            {
                return dpiX;
            }
        }

        uint systemDpi = GetDpiForSystem();
        return systemDpi == 0 ? 96u : systemDpi;
    }
}
'@

function Get-OptionalRegistryDword {
    param(
        [Parameter(Mandatory)]
        [string]$LiteralPath,
        [Parameter(Mandatory)]
        [string]$Name,
        [int]$DefaultValue
    )

    try {
        $value = (Get-ItemProperty -LiteralPath $LiteralPath -Name $Name -ErrorAction Stop).$Name
        if ($null -eq $value) {
            return $DefaultValue
        }

        return [int]$value
    }
    catch {
        return $DefaultValue
    }
}

$os = Get-CimInstance -ClassName Win32_OperatingSystem
$computer = Get-CimInstance -ClassName Win32_ComputerSystem
$processors = @(Get-CimInstance -ClassName Win32_Processor | ForEach-Object {
    [ordered]@{
        name = $_.Name.Trim()
        logicalProcessors = [int]$_.NumberOfLogicalProcessors
        maxClockMHz = [int]$_.MaxClockSpeed
    }
})
$video = @(Get-CimInstance -ClassName Win32_VideoController | ForEach-Object {
    [ordered]@{
        name = $_.Name
        driverVersion = $_.DriverVersion
        currentHorizontalResolution = $_.CurrentHorizontalResolution
        currentVerticalResolution = $_.CurrentVerticalResolution
        currentRefreshRateHz = $_.CurrentRefreshRate
    }
})
$screens = @([System.Windows.Forms.Screen]::AllScreens | ForEach-Object {
    $centerX = $_.Bounds.X + [int]($_.Bounds.Width / 2)
    $centerY = $_.Bounds.Y + [int]($_.Bounds.Height / 2)
    $effectiveDpi = [FoxMouseValidationDpi]::GetDpiAt($centerX, $centerY)
    [ordered]@{
        deviceName = $_.DeviceName
        primary = $_.Primary
        effectiveDpi = $effectiveDpi
        scalePercent = [int][Math]::Round(($effectiveDpi / 96.0) * 100)
        bounds = [ordered]@{
            x = $_.Bounds.X
            y = $_.Bounds.Y
            width = $_.Bounds.Width
            height = $_.Bounds.Height
        }
        workingArea = [ordered]@{
            x = $_.WorkingArea.X
            y = $_.WorkingArea.Y
            width = $_.WorkingArea.Width
            height = $_.WorkingArea.Height
        }
    }
})
$personalizePath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$accessibilityPath = 'HKCU:\Software\Microsoft\Accessibility'
$messageFont = [Drawing.SystemFonts]::MessageBoxFont
$textScalePercent = Get-OptionalRegistryDword `
    -LiteralPath $accessibilityPath `
    -Name 'TextScaleFactor' `
    -DefaultValue 100
$appsUseLightTheme = Get-OptionalRegistryDword `
    -LiteralPath $personalizePath `
    -Name 'AppsUseLightTheme' `
    -DefaultValue -1
$systemUsesLightTheme = Get-OptionalRegistryDword `
    -LiteralPath $personalizePath `
    -Name 'SystemUsesLightTheme' `
    -DefaultValue -1

$report = [ordered]@{
    schema = 'foxmouse.validation-environment/2'
    capturedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    os = [ordered]@{
        caption = $os.Caption
        version = $os.Version
        buildNumber = $os.BuildNumber
        architecture = $os.OSArchitecture
    }
    machine = [ordered]@{
        manufacturer = $computer.Manufacturer
        model = $computer.Model
        totalPhysicalMemoryBytes = [long]$computer.TotalPhysicalMemory
        processArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        framework = [Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
        terminalServerSession = [System.Windows.Forms.SystemInformation]::TerminalServerSession
    }
    userInterface = [ordered]@{
        culture = [Globalization.CultureInfo]::CurrentCulture.Name
        uiCulture = [Globalization.CultureInfo]::CurrentUICulture.Name
        userInteractive = [Environment]::UserInteractive
        highContrast = [System.Windows.Forms.SystemInformation]::HighContrast
        uiEffectsEnabled = [System.Windows.Forms.SystemInformation]::UIEffectsEnabled
        textScalePercent = $textScalePercent
        appsUseLightTheme = if ($appsUseLightTheme -lt 0) { $null } else { $appsUseLightTheme -ne 0 }
        systemUsesLightTheme = if ($systemUsesLightTheme -lt 0) { $null } else { $systemUsesLightTheme -ne 0 }
        messageFont = [ordered]@{
            name = $messageFont.Name
            sizeInPoints = $messageFont.SizeInPoints
        }
    }
    processors = $processors
    videoControllers = $video
    screens = $screens
}

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedOutput -Encoding utf8
Write-Output $resolvedOutput
