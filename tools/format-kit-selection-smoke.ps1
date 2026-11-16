param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this UI test from an elevated PowerShell session.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$testData = Join-Path $projectRoot ('artifacts\format-kit-test\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $testData -Force | Out-Null
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$scope = [System.Windows.Automation.TreeScope]::Descendants
$labels = Get-Content (Join-Path $projectRoot 'src\ShiftControl.App\Strings\pt-BR.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$process = Start-Process -FilePath $Executable -ArgumentList ('--data-dir="' + $testData + '"') -WorkingDirectory (Split-Path $Executable) -WindowStyle Hidden -PassThru
try {
    $window = $null
    for ($i=0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { throw 'App exited before the test.' }
        if ($process.MainWindowHandle -ne 0) {
            $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            break
        }
    }
    if (-not $window) { throw 'App window missing.' }
    function Named([string]$name) {
        $window.FindFirst($scope, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    }
    function Invoke([string]$name) {
        $element = Named $name
        if (-not $element) { throw "Control missing: $name" }
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Start-Sleep -Milliseconds 300
    }
    $nav = $null
    for ($i=0; $i -lt 120; $i++) {
        $process.Refresh()
        if ($process.HasExited) { throw 'App exited while loading hardware.' }
        if ($process.MainWindowHandle -ne 0) {
            $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            $nav = Named $labels.'nav.formatKit'
            if ($nav) { break }
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $nav) { throw 'Format Kit navigation missing.' }
    $nav.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Seconds 2
    $loaded = $false
    for ($i=0; $i -lt 120; $i++) {
        $texts = $window.FindAll($scope, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
        if (@($texts | Where-Object { $_.Current.Name -match '^\d+ marcados' }).Count -gt 0) { $loaded=$true; break }
        Start-Sleep -Milliseconds 500
    }
    if (-not $loaded) { throw 'Package detection did not finish.' }
    Invoke $labels.'formatKit.clearSelection'
    $zero = Named ($labels.'formatKit.upgradeSelected' -f 0)
    if (-not $zero -or $zero.Current.IsEnabled) { throw 'Empty selection must disable updates.' }
    $checkBoxes = $window.FindAll($scope, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
    if ($checkBoxes.Count -eq 0) { throw 'Package checkboxes missing.' }
    $candidates = @()
    foreach ($box in $checkBoxes) {
        $toggle = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Clear did not uncheck every package.' }
        $row = $box
        for ($depth=0; $depth -lt 8; $depth++) {
            $row = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($row)
            if (-not $row) { break }
            $peers = $row.FindAll($scope, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
            if ($peers.Count -gt 1) { break }
            $texts = $row.FindAll($scope, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
            if (@($texts | Where-Object { $_.Current.Name.Contains([char]0x2192) }).Count -gt 0) { $candidates += $box; break }
        }
    }
    if ($candidates.Count -eq 0) {
        @{ emptySelectionVerified=$true; positiveSelectionVerified=$false; checkboxes=$checkBoxes.Count; realProgramsUpdated=0; reason='No available update rows located; positive selection requires an eligible package on this PC.'; dataDirectory=$testData } |
            ConvertTo-Json | Set-Content (Join-Path $projectRoot 'artifacts\format-kit-selection-report.json') -Encoding UTF8
        Write-Warning 'Empty selection verified; positive selection case not exercised. See PackageJobPlannerTests for deterministic coverage.'
        return
    }
    $selected = @($candidates | Select-Object -First 2)
    foreach ($box in $selected) { $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
    Start-Sleep -Milliseconds 500
    $update = Named ($labels.'formatKit.upgradeSelected' -f $selected.Count)
    if (-not $update -or -not $update.Current.IsEnabled) { throw 'Selected update count does not match checkboxes.' }
    foreach ($box in $selected) { $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
    Start-Sleep -Milliseconds 500
    $zero = Named ($labels.'formatKit.upgradeSelected' -f 0)
    if (-not $zero -or $zero.Current.IsEnabled) { throw 'Unchecking updates must disable the button again.' }
    @{ emptySelectionVerified=$true; positiveSelectionVerified=$true; selectedUpdatesTested=$selected.Count; checkboxes=$checkBoxes.Count; realProgramsUpdated=0; dataDirectory=$testData } |
        ConvertTo-Json | Set-Content (Join-Path $projectRoot 'artifacts\format-kit-selection-report.json') -Encoding UTF8
    Write-Output 'Format Kit selection verified without starting any install/update operation.'
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
}
