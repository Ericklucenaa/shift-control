param([string]$Executable, [switch]$Narrow)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $projectRoot 'src\ShiftControl.App\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\ShiftControl.exe' }
$dataPath = Join-Path $projectRoot ('artifacts\appearance-tests\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
$labels = Get-Content (Join-Path $projectRoot 'src\ShiftControl.App\Strings\pt-BR.json') -Encoding UTF8 -Raw | ConvertFrom-Json
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class AppearanceNative {
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
 [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point p);
 [StructLayout(LayoutKind.Sequential)] public struct Point { public int X,Y; }
}
'@
$cursor = New-Object AppearanceNative+Point
[AppearanceNative]::GetCursorPos([ref]$cursor) | Out-Null
$process = Start-Process -FilePath $Executable -ArgumentList ('--page=settings --data-dir="' + $dataPath + '"') -WindowStyle Hidden -PassThru
try {
    for ($i=0; $i -lt 50; $i++) { Start-Sleep -Milliseconds 300; $process.Refresh(); if ($process.MainWindowHandle -ne 0) { break } }
    if ($process.MainWindowHandle -eq 0) { throw 'Janela não encontrada.' }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    $width = if ($Narrow) { 800 } else { 1360 }
    [AppearanceNative]::SetWindowPos($process.MainWindowHandle, [IntPtr](-1), 30, 30, $width, 880, 0x0040) | Out-Null
    Start-Sleep -Seconds 1
    function Find-Control($name, $type) {
        $condition = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)))
        $element = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if (-not $element) { throw "Controle não encontrado: $name" }
        return $element
    }
    function Select-Option($header, $value) {
        $combo = Find-Control $header ([System.Windows.Automation.ControlType]::ComboBox)
        $combo.SetFocus()
        Start-Sleep -Milliseconds 100
        $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        Start-Sleep -Milliseconds 120
        $condition = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $value)),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)))
        $items = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
        $selected = $false
        foreach ($item in $items) {
            $pattern = $null
            if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) { $pattern.Select(); $selected = $true; break }
        }
        if (-not $selected) { throw "Opção não encontrada: $value" }
        Start-Sleep -Milliseconds 150
        try { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
    }
    function Capture($file) {
        $rect = $window.Current.BoundingRectangle
        $bitmap = New-Object System.Drawing.Bitmap([int]$rect.Width,[int]$rect.Height)
        $g = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc = $g.GetHdc()
            try { [AppearanceNative]::PrintWindow($process.MainWindowHandle,$dc,2) | Out-Null } finally { $g.ReleaseHdc($dc) }
            $bitmap.Save((Join-Path $dataPath $file))
        } finally { $g.Dispose(); $bitmap.Dispose() }
    }
    $report = @()
    foreach ($mode in @('dark','light')) {
        Select-Option $labels.'settings.theme' $labels."settings.theme.$mode"
        foreach ($palette in @('Graphite','Ocean','Forest','Plum','Sand')) {
            Select-Option $labels.'appearance.palette' $labels."appearance.palette.$palette"
            foreach ($look in @('Classic','Soft','Outline','Liquid')) {
                Select-Option $labels.'appearance.buttons' $labels."appearance.button.$look"
                $primary = Find-Control $labels.'appearance.primary' ([System.Windows.Automation.ControlType]::Button)
                $primary.SetFocus()
                Start-Sleep -Milliseconds 100
                if (-not $primary.Current.IsEnabled -or $primary.Current.IsOffscreen) { throw 'Prévia principal indisponível.' }
                $rect = $primary.Current.BoundingRectangle
                [AppearanceNative]::SetCursorPos([int]($rect.X+$rect.Width/2),[int]($rect.Y+$rect.Height/2)) | Out-Null
                Start-Sleep -Milliseconds 200
                Capture "$mode-$palette-$look.png"
                $report += [pscustomobject]@{ Mode=$mode; Palette=$palette; Buttons=$look; Visible=$true }
                [AppearanceNative]::SetCursorPos(20,20) | Out-Null
            }
        }
    }
    $report | ConvertTo-Json | Set-Content (Join-Path $dataPath 'report.json') -Encoding UTF8
    Write-Output "Combinações verificadas: $($report.Count). Dados: $dataPath"
} finally {
    [AppearanceNative]::SetCursorPos($cursor.X,$cursor.Y) | Out-Null
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
