param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference = 'Stop'
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
    for ($i=0; $i -lt 120; $i++) {
        $loading = Named $labels.'formatKit.loading'
        if (-not $loading -or $loading.Current.IsOffscreen) { break }
        Start-Sleep -Milliseconds 500
    }
    Invoke $labels.'formatKit.clearSelection'
    $zero = Named ($labels.'formatKit.upgradeSelected' -f 0)
    if (-not $zero -or $zero.Current.IsEnabled) { throw 'Empty selection must disable updates.' }
    $checkBoxes = $window.FindAll($scope, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)))
    if ($checkBoxes.Count -eq 0) { throw 'Package checkboxes missing.' }
    $candidates = @()
    foreach ($box in $checkBoxes) {
        $toggle = $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) { throw 'Clear did not uncheck every package.' }
        $row = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($box)
        $texts = $row.FindAll($scope, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
        if (@($texts | Where-Object { $_.Current.Name -like '*dispon*' -and $_.Current.Name -like '* *' }).Count -gt 0) { $candidates += $box }
    }
    if ($candidates.Count -eq 0) { throw 'No available update to validate the selected counter on this PC.' }
    $selected = @($candidates | Select-Object -First 2)
    foreach ($box in $selected) { $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
    Start-Sleep -Milliseconds 500
    $update = Named ($labels.'formatKit.upgradeSelected' -f $selected.Count)
    if (-not $update -or -not $update.Current.IsEnabled) { throw 'Selected update count does not match checkboxes.' }
    foreach ($box in $selected) { $box.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle() }
    Start-Sleep -Milliseconds 500
    $zero = Named ($labels.'formatKit.upgradeSelected' -f 0)
    if (-not $zero -or $zero.Current.IsEnabled) { throw 'Unchecking updates must disable the button again.' }
    @{ ok=$true; selectedUpdatesTested=$selected.Count; checkboxes=$checkBoxes.Count; realProgramsUpdated=0; dataDirectory=$testData } |
        ConvertTo-Json | Set-Content (Join-Path $projectRoot 'artifacts\format-kit-selection-report.json') -Encoding UTF8
    Write-Output 'Format Kit selection verified without starting any install/update operation.'
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
}
