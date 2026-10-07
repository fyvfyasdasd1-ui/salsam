param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $ExePath
)

# Saved as UTF-8 WITH BOM: Windows PowerShell 5.1 otherwise reads Cyrillic as ANSI.
# Run on a Windows desktop: powershell.exe -NoProfile -File scripts\Smoke-Portable.ps1 -ExePath artifacts\portable-x64\Salsam.exe
# This checks the bundled GUI and live CPU/RAM readings without changing settings.
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

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        throw 'This smoke check requires Windows and Windows PowerShell 5.1.'
    }
    Add-Type -AssemblyName UIAutomationClient
    Add-Type -AssemblyName UIAutomationTypes
    Add-Type -AssemblyName WindowsBase
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
    Assert-Text ([string] $main.Current.Name) 'Salsam 2.0 · Настройте Windows под себя' 'main application title'
    Assert-Text (Visible-Name (Find-Id $main 'SectionTitle')) 'Обзор' 'initial Russian section heading'
    Assert-Text (Visible-Name (Find-Id $main 'ChangeQueue')) 'План изменений' 'change queue heading'
    $content = Find-Id $main 'Content'
    if ($null -eq $content) { throw 'Main content area is missing from UI Automation.' }
    Assert-VisibleContentText $content "Больше контроля.`nМеньше лишнего."
    Write-Output "PASS portable main window: $($main.Current.Name); Обзор; План изменений"

    $navigation = Find-Id $main 'Navigation'
    $monitorName = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Мониторинг')
    $itemType = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $itemCondition = [System.Windows.Automation.AndCondition]::new($monitorName, $itemType)
    $monitorItem = $navigation.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $itemCondition)
    if ($null -eq $monitorItem) { throw 'Russian sidebar item Мониторинг was not found.' }
    $selection = $null
    if (-not $monitorItem.TryGetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern, [ref] $selection)) {
        throw 'Мониторинг does not support SelectionItemPattern; navigation could not be exercised.'
    }
    $selection.Select()
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
