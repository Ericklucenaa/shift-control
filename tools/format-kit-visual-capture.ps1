param([string]$Exe, [string]$Out, [string]$Strings)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$data = Join-Path $Out 'data'
$L = Get-Content $Strings -Raw -Encoding UTF8 | ConvertFrom-Json
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class QaNative {
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
$p = Start-Process -FilePath $Exe -ArgumentList "--page=formatkit `"--data-dir=$data`"" -PassThru
$scope = [System.Windows.Automation.TreeScope]::Descendants
function Find($name) { $w.FindFirst($scope, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name))) }
function Shot($name, $width) {
  [QaNative]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, 40, 40, $width, 900, 0x0040) | Out-Null
  Start-Sleep -Milliseconds 1200
  $r = New-Object QaNative+RECT; [QaNative]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
  $b = New-Object System.Drawing.Bitmap(($r.R - $r.L), ($r.B - $r.T)); $g = [System.Drawing.Graphics]::FromImage($b)
  $dc = $g.GetHdc(); [QaNative]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc)
  $b.Save((Join-Path $Out "$name.png")); $g.Dispose(); $b.Dispose(); "captured $name"
}
try {
  for ($i = 0; $i -lt 90; $i++) { $p.Refresh(); if ($p.HasExited) { throw "app exited code $($p.ExitCode)" }
    if ($p.MainWindowHandle -ne 0) { $w = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle); if (Find $L.'formatKit.search') { break } }
    Start-Sleep -Milliseconds 500 }
  if (-not (Find $L.'formatKit.search')) { throw 'Format Kit did not open' }
  # espera a detecção do winget terminar (Limpar seleção habilita)
  for ($i = 0; $i -lt 180; $i++) { if ((Find $L.'formatKit.clearSelection').Current.IsEnabled) { break }; Start-Sleep -Milliseconds 500 }
  "detection finished: $((Find $L.'formatKit.clearSelection').Current.IsEnabled)"
  Shot '1-wide' 1360
  Shot '2-narrow' 800
  $search = (Find $L.'formatKit.search').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
  $search.SetValue('zzz-nada'); Start-Sleep -Milliseconds 600
  "empty state visible: $([bool](Find $L.'formatKit.noResults'))"
  Shot '3-empty-wide' 1360
  $search.SetValue('7-zip'); Start-Sleep -Milliseconds 600
  Shot '4-filtered-narrow' 800
  $clear = Find $L.'formatKit.clearSearch'
  "clear button present while results exist: $([bool]$clear)"
} finally { if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep 2; if (-not $p.HasExited) { $p.Kill() } } }
