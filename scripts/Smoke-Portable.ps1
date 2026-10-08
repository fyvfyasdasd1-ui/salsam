param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ExePath
)

# Saved as UTF-8 WITH BOM: Windows PowerShell 5.1 otherwise reads Cyrillic as ANSI.
# Run on a Windows desktop: powershell.exe -NoProfile -File scripts\Smoke-Portable.ps1 -ExePath artifacts\portable-x64\Salsam.exe
# This checks the GUI, live CPU/RAM, and displayed native settings without changing Windows settings.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$appProcess = $null
$clock = [Diagnostics.Stopwatch]::StartNew()
# Leave time for cleanup inside the overall 60-second limit.
$deadlineSeconds = 55

function Assert-Running {
    $appProcess.Refresh()
    if ($appProcess.HasExited) {
        throw "Portable EXE exited before the GUI check completed (exit code $($appProcess.ExitCode))."
    }
    if ($clock.Elapsed.TotalSeconds -ge $deadlineSeconds) {
        throw 'GUI smoke check timed out. An accessible interactive Windows desktop is required; missing UI Automation content is a failure.'
    }
}

function Find-Id {
    param([System.Windows.Automation.AutomationElement] $Root, [string] $Id)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Visible-Name {
    param([System.Windows.Automation.AutomationElement] $Element)
    if ($null -eq $Element -or $Element.Current.IsOffscreen) { return '' }
    return [string] $Element.Current.Name
}

function Get-AppWindows {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int] $appProcess.Id)
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children, $condition)
    # Keep the empty collection intact instead of turning it into $null.
    return ,$windows
}

function Assert-NoErrorWindow {
    param($Windows)
    foreach ($window in $Windows) {
        $name = [string] $window.Current.Name
        $pattern = $null
        $modal = $window.TryGetCurrentPattern(
            [System.Windows.Automation.WindowPattern]::Pattern, [ref] $pattern) -and $pattern.Current.IsModal
        if ($name -match '(?i)(ошибка|error|\.NET|runtime)' -or $modal) {
            $text = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Automation]::ControlViewCondition)
            $details = @($text | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join ' | '
            if ($details.Length -gt 800) { $details = $details.Substring(0, 800) }
            throw "Startup/error dialog detected: '$name'. $details"
        }
    }
}

function Assert-VisibleContentText {
    param([System.Windows.Automation.AutomationElement] $Root, [string] $Expected)
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Expected)
    # Collapsed sections can remain in the raw UIA tree and share text labels.
    # Require an exact, visible match within the real main content area.
    $elements = $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
    foreach ($element in $elements) {
        if ((Visible-Name $element) -eq $Expected) { return }
    }
    throw "Visible content text is missing: '$Expected'."
}

function Assert-Text {
    param([string] $Actual, [string] $Expected, [string] $Description)
    if ($Actual -ne $Expected) {
        throw "Missing or incorrect $Description. Expected '$Expected'; received '$Actual'."
    }
}

function Select-Section {
    param([System.Windows.Automation.AutomationElement] $Navigation, [string] $Name)
    $nameCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $itemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $condition = [System.Windows.Automation.AndCondition]::new($nameCondition, $itemCondition)
    $item = $Navigation.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($null -eq $item) { throw "Russian sidebar item '$Name' was not found." }
    $selection = $null
    if (-not $item.TryGetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern, [ref] $selection)) {
        throw "'$Name' does not support SelectionItemPattern; navigation could not be exercised."
    }
    $selection.Select()
}

function Assert-NativeTweakState {
    param(
        [System.Windows.Automation.AutomationElement] $Main,
        [System.Windows.Automation.AutomationElement] $Content,
        [System.Windows.Automation.ValuePattern] $Search,
        [string] $Id,
        [string] $Query,
        [bool] $Enabled
    )
    function Get-FailureCatalogDiagnostic {
        # Called only on failure. Inspect this application's catalog, never the
        # desktop or another process; limit output to twelve tweak IDs, retaining
        # IsOffscreen to distinguish a missing tree from clipped/hidden content.
        try {
            $failureCatalog = Find-Id $Main 'TweakCatalog'
            if ($null -eq $failureCatalog) { return 'Catalog UIA: TweakCatalog missing.' }
            $children = $failureCatalog.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Automation]::RawViewCondition)
            $actualIdFound = $null -ne (Find-Id $Main ("TweakState_" + $Id))
            $summary = "Catalog UIA: IsOffscreen=$($failureCatalog.Current.IsOffscreen); childCount=$($children.Count); actualIdFound=$actualIdFound."
            $details = [System.Collections.Generic.List[string]]::new()
            foreach ($child in $children) {
                $childId = [string] $child.Current.AutomationId
                if ($childId.StartsWith('Tweak', [StringComparison]::Ordinal)) {
                    $childName = ([string] $child.Current.Name).Replace("`r", ' ').Replace("`n", ' ')
                    if ($childId.Length -gt 100) { $childId = $childId.Substring(0, 100) }
                    if ($childName.Length -gt 120) { $childName = $childName.Substring(0, 120) }
                    $details.Add("$childId='$childName' (IsOffscreen=$($child.Current.IsOffscreen))")
                    if ($details.Count -ge 12) { break }
                }
            }
            if ($details.Count -eq 0) { return "$summary No descendants with a Tweak-prefixed AutomationId." }
            return $summary + ' Tweak descendants (up to 12): ' + ($details -join ' | ')
        }
        catch { return 'Catalog UIA diagnostic could not be read.' }
    }
    # Filtering exercises the actual user search and brings the tested row into
    # view without selecting a tweak, queuing it, or changing a Windows setting.
    $Search.SetValue($Query)
    $expected = if ($Enabled) { 'Включено' } else { 'Уже выключено' }
    $actual = ''
    while ($clock.Elapsed.TotalSeconds -lt $deadlineSeconds) {
        Assert-Running
        Assert-NoErrorWindow (Get-AppWindows)
        $catalog = Find-Id $Main 'TweakCatalog'
        $scroll = $null
        if ($Content.TryGetCurrentPattern(
            [System.Windows.Automation.ScrollPattern]::Pattern, [ref] $scroll) -and
            $scroll.Current.VerticallyScrollable) {
            $scroll.SetScrollPercent([System.Windows.Automation.ScrollPattern]::NoScroll, 0)
        }
        $state = Find-Id $Main ("TweakState_" + $Id)
        $actual = Visible-Name $state
        if ($null -ne $catalog -and $actual -ceq $expected) {
            Write-Output "PASS visible native setting '$Id': $actual (independent read-only Windows API)."
            return
        }
        if ($actual -ceq 'Включено' -or $actual -ceq 'Уже выключено') {
            throw "Displayed setting '$Id' disagrees with Windows. Expected '$expected'; received '$actual'. $(Get-FailureCatalogDiagnostic)"
        }
        Start-Sleep -Milliseconds 150
    }
    throw "Optimization catalog did not expose a visible native state for '$Id'. Expected '$expected'; received '$actual'. Missing, hidden, unavailable, or placeholder states fail this check. $(Get-FailureCatalogDiagnostic)"
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'This smoke check requires Windows and Windows PowerShell 5.1.'
    }
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName WindowsBase
    # Independent probes expose GET operations only. BOOL is a 32-bit integer;
    # SPI_GETMOUSE receives exactly three 32-bit integers. There are no SET APIs.
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class SalsamSmokeNative
{
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadBoolean(uint action, uint parameter, out int value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadMouse(uint action, uint parameter,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 3)] int[] values, uint flags);

    public static bool MenuAnimationEnabled()
    {
        int value;
        if (!ReadBoolean(0x1002, 0, out value, 0)) // SPI_GETMENUANIMATION
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_GETMENUANIMATION failed.");
        return value != 0;
    }

    public static bool MouseAccelerationEnabled()
    {
        int[] values = new int[3];
        if (!ReadMouse(0x0003, 0, values, 0)) // SPI_GETMOUSE
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_GETMOUSE failed.");
        if (values[0] < 0 || values[1] < 0 || values[2] < 0 || values[2] > 2)
            throw new InvalidOperationException("Windows returned an invalid mouse acceleration vector.");
        return values[2] != 0;
    }
}
'@
    $exe = (Resolve-Path -LiteralPath $ExePath).ProviderPath
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf) -or [IO.Path]::GetExtension($exe) -ne '.exe') {
        throw 'ExePath must identify the actual published portable .exe.'
    }

    # Ordinary launch: no RunAs verb, administrator request, installer, or wrapper.
    $appProcess = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru
    $main = $null
    $lastTitles = ''
    while ($clock.Elapsed.TotalSeconds -lt 25) {
        Assert-Running
        $windows = Get-AppWindows
        Assert-NoErrorWindow $windows
        $lastTitles = @($windows | ForEach-Object { $_.Current.Name }) -join ' | '
        foreach ($window in $windows) {
            if ($window.Current.Name -match '(?i)^salsam\b' -and
                $null -ne (Find-Id $window 'Navigation') -and
                $null -ne (Find-Id $window 'ChangeQueue') -and
                $null -ne (Find-Id $window 'SectionTitle')) {
                $main = $window
                break
            }
        }
        if ($null -ne $main) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $main) {
        throw "Portable EXE did not expose the expected main GUI through UI Automation within 25 seconds. Visible process windows: '$lastTitles'. An inaccessible desktop does not count as a pass."
    }
    Assert-Text ([string] $main.Current.Name) 'Salsam 2.1 · Настройте Windows под себя' 'main application title'
    Assert-Text (Visible-Name (Find-Id $main 'SectionTitle')) 'Обзор' 'initial Russian section heading'
    Assert-Text (Visible-Name (Find-Id $main 'ChangeQueue')) 'План изменений' 'change queue heading'
    $content = Find-Id $main 'Content'
    if ($null -eq $content) { throw 'Main content area is missing from UI Automation.' }
    Assert-VisibleContentText $content "Больше контроля.`nМеньше лишнего."
    Write-Output "PASS portable main window: $($main.Current.Name); Обзор; План изменений"

    $navigation = Find-Id $main 'Navigation'
    Select-Section $navigation 'Мониторинг'
    $monitoringClock = [Diagnostics.Stopwatch]::StartNew()
    $cpu = ''
    $ram = ''
    $readingsValid = $false
    while ($clock.Elapsed.TotalSeconds -lt $deadlineSeconds) {
        Assert-Running
        Assert-NoErrorWindow (Get-AppWindows)
        $section = Visible-Name (Find-Id $main 'SectionTitle')
        $cpu = Visible-Name (Find-Id $main 'CpuValue')
        $ram = Visible-Name (Find-Id $main 'RamValue')
        if ($monitoringClock.Elapsed.TotalSeconds -ge 12 -and $section -eq 'Мониторинг') {
            $cpuMatch = [regex]::Match($cpu, '^\s*(?<percent>\d{1,3}(?:[.,]\d+)?)\s*%\s*$')
            $ramMatch = [regex]::Match($ram, '^\s*(?<percent>\d{1,3}(?:[.,]\d+)?)\s*%\s*·\s*(?<used>\d+(?:[.,]\d+)?)\s*/\s*(?<total>\d+(?:[.,]\d+)?)\s*ГБ\s*$')
            if ($cpuMatch.Success -and $ramMatch.Success) {
                $culture = [Globalization.CultureInfo]::InvariantCulture
                $cpuPercent = [double]::Parse($cpuMatch.Groups['percent'].Value.Replace(',', '.'), $culture)
                $ramPercent = [double]::Parse($ramMatch.Groups['percent'].Value.Replace(',', '.'), $culture)
                $used = [double]::Parse($ramMatch.Groups['used'].Value.Replace(',', '.'), $culture)
                $total = [double]::Parse($ramMatch.Groups['total'].Value.Replace(',', '.'), $culture)
                if ($cpuPercent -ge 0 -and $cpuPercent -le 100 -and $ramPercent -ge 0 -and
                    $ramPercent -le 100 -and $total -gt 0 -and $used -ge 0 -and $used -le ($total + 0.11)) {
                    $readingsValid = $true
                    break
                }
            }
        }
        Start-Sleep -Milliseconds 250
    }
    if (-not $readingsValid) {
        throw "Мониторинг did not expose valid measured CPU/RAM values after warmup. CPU='$cpu'; RAM='$ram'. Unavailable, placeholder, or hidden values fail this check."
    }
    Assert-Text (Visible-Name (Find-Id $main 'SectionTitle')) 'Мониторинг' 'selected Russian section'
    Assert-VisibleContentText $content 'Показатели системы'
    Assert-VisibleContentText $content 'ПРОЦЕССОР'
    Assert-VisibleContentText $content 'ОПЕРАТИВНАЯ ПАМЯТЬ'
    Write-Output "PASS sidebar SelectionItemPattern -> Мониторинг; live CPU=$cpu; RAM=$ram"

    Select-Section $navigation 'Оптимизация'
    $search = $null
    while ($clock.Elapsed.TotalSeconds -lt $deadlineSeconds) {
        Assert-Running
        Assert-NoErrorWindow (Get-AppWindows)
        $searchBox = Find-Id $main 'TweakSearch'
        if ((Visible-Name (Find-Id $main 'SectionTitle')) -eq 'Оптимизация' -and
            $null -ne (Find-Id $main 'TweakCatalog') -and $null -ne $searchBox -and
            $searchBox.TryGetCurrentPattern(
                [System.Windows.Automation.ValuePattern]::Pattern, [ref] $search)) {
            break
        }
        Start-Sleep -Milliseconds 150
    }
    if ($null -eq $search) { throw 'Оптимизация did not expose its searchable TweakCatalog.' }
    Assert-Text (Visible-Name (Find-Id $main 'SectionTitle')) 'Оптимизация' 'optimization section heading'
    Assert-NativeTweakState $main $content $search 'menu-animation' 'Анимация меню' ([SalsamSmokeNative]::MenuAnimationEnabled())
    Assert-NativeTweakState $main $content $search 'mouse-acceleration' 'Ускорение указателя мыши' ([SalsamSmokeNative]::MouseAccelerationEnabled())
    $search.SetValue('')

    # Optional review artifact. A capture problem never substitutes for GUI checks.
    if ($clock.Elapsed.TotalSeconds -lt 50) {
        $bitmap = $null
        $graphics = $null
        try {
            Add-Type -AssemblyName System.Drawing
            $rect = $main.Current.BoundingRectangle
            if ($rect.IsEmpty -or $rect.Width -le 0 -or $rect.Height -le 0) { throw 'Window has no visible capture bounds.' }
            $width = [int] [Math]::Ceiling($rect.Width)
            $height = [int] [Math]::Ceiling($rect.Height)
            $bitmap = [Drawing.Bitmap]::new($width, $height)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            $graphics.CopyFromScreen([int] $rect.X, [int] $rect.Y, 0, 0, $bitmap.Size,
                [Drawing.CopyPixelOperation]::SourceCopy)
            $imageDirectory = Split-Path -Parent (Split-Path -Parent $exe)
            $imagePath = Join-Path $imageDirectory 'smoke.png'
            $bitmap.Save($imagePath, [Drawing.Imaging.ImageFormat]::Png)
            Write-Output "GUI review image: $imagePath"
        }
        catch { Write-Warning "Optional GUI image unavailable: $($_.Exception.Message)" }
        finally {
            if ($null -ne $graphics) { $graphics.Dispose() }
            if ($null -ne $bitmap) { $bitmap.Dispose() }
        }
    }
    Write-Output 'PASS published portable GUI smoke check (no system settings changed).'
}
finally {
    # Only clean up the exact process started by this script; never kill by name.
    if ($null -ne $appProcess) {
        try {
            $appProcess.Refresh()
            if (-not $appProcess.HasExited) {
                [void] $appProcess.CloseMainWindow()
                if (-not $appProcess.WaitForExit(1000)) {
                    $appProcess.Kill()
                    [void] $appProcess.WaitForExit(1000)
                }
            }
        }
        catch { Write-Warning "Could not close the launched GUI process: $($_.Exception.Message)" }
        $appProcess.Dispose()
    }
    $clock.Stop()
}
