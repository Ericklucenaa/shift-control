param([Parameter(Mandatory=$true)][string]$Executable)
$ErrorActionPreference='Stop'
$principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Use an elevated PowerShell session for the administrator app.' }
$projectRoot=Split-Path -Parent $PSScriptRoot
$dataPath=Join-Path $projectRoot ('artifacts\ui-refresh\qa-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
$labels=Get-Content (Join-Path $projectRoot 'src\ShiftControl.App\Strings\pt-BR.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing,System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class KitLayoutNative {
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int command);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
}
'@
$scope=[System.Windows.Automation.TreeScope]::Descendants
$process=Start-Process -FilePath $Executable -ArgumentList ('--page=formatkit --data-dir="'+$dataPath+'"') -WindowStyle Hidden -PassThru
try {
    $window=$null
    function Find([string]$name) { $window.FindFirst($scope,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$name))) }
    function Button([string]$name) {
        $item=Find $name
        if (-not $item) { throw "Missing control: $name" }
        return $item
    }
    function Invoke([string]$name) { (Button $name).GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke();Start-Sleep -Milliseconds 350 }
    function Navigate([string]$name) { (Button $name).GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select();Start-Sleep -Milliseconds 600 }
    function Capture([string]$name) {
        Start-Sleep -Milliseconds 400
        $r=$window.Current.BoundingRectangle
        $b=New-Object System.Drawing.Bitmap([int]$r.Width,[int]$r.Height)
        $g=[System.Drawing.Graphics]::FromImage($b)
        try {
            $dc=$g.GetHdc()
            try { if (-not [KitLayoutNative]::PrintWindow($process.MainWindowHandle,$dc,2)) { throw 'Capture failed.' } }
            finally { $g.ReleaseHdc($dc) }
            $b.Save((Join-Path $dataPath ($name+'.png')))
        } finally { $g.Dispose();$b.Dispose() }
    }
    for ($i=0;$i -lt 120;$i++) {
        $process.Refresh()
        if ($process.HasExited) { throw 'App exited during startup.' }
        if ($process.MainWindowHandle -ne 0) {
            $window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            if (Find $labels.'formatKit.search') { break }
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not $window -or -not (Find $labels.'formatKit.search')) { throw 'Format Kit did not open.' }
    [KitLayoutNative]::ShowWindow($process.MainWindowHandle,9) | Out-Null
    [KitLayoutNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    for ($i=0;$i -lt 120;$i++) {
        if ((Button $labels.'formatKit.clearSelection').Current.IsEnabled) { break }
        Start-Sleep -Milliseconds 500
    }
    if (-not (Button $labels.'formatKit.clearSelection').Current.IsEnabled) { throw 'Detection did not finish.' }
    $report=@()
    foreach ($width in @(1360,800)) {
        [KitLayoutNative]::SetWindowPos($process.MainWindowHandle,[IntPtr]::Zero,30,30,$width,880,0x0040) | Out-Null
        Start-Sleep -Milliseconds 600
        Invoke $labels.'formatKit.selectAll'
        $upgradeName=@($window.FindAll($scope,[System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like 'Atualizar marcados (*)' })[0].Current.Name
        Capture "dark-$width-list"
        $search=Button $labels.'formatKit.search'
        $value=$search.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $value.SetValue('__no_matching_package__');Start-Sleep -Milliseconds 500
        $empty=Button $labels.'formatKit.noResults'
        if ($empty.Current.IsOffscreen) { throw 'Empty state is not visible.' }
        $texts=$window.FindAll($scope,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)))
        $hidden=@($texts | Where-Object { $_.Current.Name -match '^\d+ marcados fora' })
        if ($hidden.Count -eq 0) { throw 'Hidden selection warning missing.' }
        if (-not (Find $upgradeName)) { throw 'Search changed the selected update count.' }
        Capture "dark-$width-empty"
        # Actual keyboard activation of a harmless action.
        (Button $labels.'formatKit.clearSearch').SetFocus()
        [System.Windows.Forms.SendKeys]::SendWait(' ')
        Start-Sleep -Milliseconds 400
        if ((Button $labels.'formatKit.search').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '') { throw 'Keyboard clear-search failed.' }
        if (-not (Button $labels.'formatKit.search').Current.HasKeyboardFocus) { throw 'Search did not regain focus.' }
        Invoke $labels.'formatKit.clearSelection'
        $zero=Button ($labels.'formatKit.upgradeSelected' -f 0)
        if ($zero.Current.IsEnabled) { throw 'Empty selection enabled update.' }
        Capture "dark-$width-cleared"
        $report+=@{ width=$width; emptyState=$true; hiddenSelection=$true; keyboardClearSearch=$true; selectionPreserved=$true; updateDisabledWhenEmpty=$true }
    }
    [KitLayoutNative]::SetWindowPos($process.MainWindowHandle,[IntPtr]::Zero,30,30,1360,880,0x0040) | Out-Null
    Navigate $labels.'nav.settings'
    $combo=Button $labels.'settings.theme'
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 300
    $items=[System.Windows.Automation.AutomationElement]::RootElement.FindAll($scope,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$labels.'settings.theme.light')))
    $selected=$false
    foreach ($item in $items) {
        $pattern=$null
        if ($item.Current.ProcessId -eq $process.Id -and $item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) { $pattern.Select();$selected=$true;break }
    }
    if (-not $selected) { throw 'Light theme option not found.' }
    Navigate $labels.'nav.formatKit'
    Capture 'light-1360-list'
    [KitLayoutNative]::SetWindowPos($process.MainWindowHandle,[IntPtr]::Zero,30,30,800,880,0x0040) | Out-Null
    Capture 'light-800-list'
    @{ ok=$true; checks=$report; lightThemeCaptured=$true; realProgramsUpdated=0; directory=$dataPath } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $dataPath 'report.json') -Encoding UTF8
    Write-Output "QA complete: $dataPath"
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
}
