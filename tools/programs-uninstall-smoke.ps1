# Teste de ponta a ponta da tela "Programas instalados" com um programa FALSO registrado só no usuário atual (HKCU).
# Nada do sistema é desinstalado. Uso: programs-uninstall-smoke.ps1 -Executable <ShiftControl.exe> -Out <pasta>
param([Parameter(Mandatory = $true)][string]$Executable, [Parameter(Mandatory = $true)][string]$Out)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$name = 'Programa Teste ShiftQA'
$key = 'ShiftQA-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$key"
$leftDir = Join-Path $env:LOCALAPPDATA $name
$leftKey = "HKCU:\Software\$name"
$data = Join-Path $Out 'data'
$labels = Get-Content (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\ShiftControl.App\Strings\pt-BR.json') -Raw -Encoding UTF8 | ConvertFrom-Json

# Desinstalador falso: dispara um filho e sai na hora (como o Inno Setup); o filho apaga a entrada ~5 s depois.
$delete = "reg delete `"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\$key`" /f"
New-Item -Path $uninstallKey -Force | Out-Null
Set-ItemProperty $uninstallKey DisplayName $name
Set-ItemProperty $uninstallKey DisplayVersion '1.2.3'
Set-ItemProperty $uninstallKey Publisher 'ShiftQA Labs'
Set-ItemProperty $uninstallKey InstallDate (Get-Date -Format yyyyMMdd)
New-ItemProperty $uninstallKey EstimatedSize -Value 5120 -PropertyType DWord -Force | Out-Null
Set-ItemProperty $uninstallKey UninstallString "cmd.exe /c start `"`" /b cmd.exe /c `"ping -n 6 127.0.0.1 >nul & $delete >nul`""
New-Item -ItemType Directory -Force -Path $leftDir | Out-Null
Set-Content (Join-Path $leftDir 'config.ini') ('x' * 2048)
New-Item -Path $leftKey -Force | Out-Null
Set-ItemProperty $leftKey Theme 'dark'

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class ProgQa {
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int hh, uint f);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
$A = [System.Windows.Automation.AutomationElement]
$D = [System.Windows.Automation.TreeScope]::Descendants
function ById($root, $id) { $root.FindFirst($D, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id))) }
function ByName($root, $n) { $root.FindAll($D, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $n))) }
function Wait-For([scriptblock]$probe, [int]$ms = 20000, [string]$what) {
  for ($t = 0; $t -lt $ms; $t += 250) { $r = & $probe; if ($r) { return $r }; Start-Sleep -Milliseconds 250 }
  throw "Timeout esperando: $what"
}
function Invoke-El($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 400 }
function Shot([string]$file, [int]$width) {
  [ProgQa]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, 40, 40, $width, 900, 0x0040) | Out-Null; Start-Sleep -Milliseconds 900
  $r = New-Object ProgQa+RECT; [ProgQa]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
  $b = New-Object System.Drawing.Bitmap(($r.R - $r.L), ($r.B - $r.T)); $g = [System.Drawing.Graphics]::FromImage($b)
  $dc = $g.GetHdc(); [ProgQa]::PrintWindow($p.MainWindowHandle, $dc, 2) | Out-Null; $g.ReleaseHdc($dc)
  $b.Save((Join-Path $Out $file)); $g.Dispose(); $b.Dispose()
}
# O primeiro botão com o nome dado que esteja dentro de um diálogo (o botão da barra tem AutomationId próprio).
function Dialog-Button([string]$text) {
  foreach ($e in (ByName $w $text)) { if ($e.Current.AutomationId -notlike 'programs.*' -and $e.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button) { return $e } }
  return $null
}

$p = Start-Process -FilePath $Executable -ArgumentList "--page=programs `"--data-dir=$data`"" -PassThru
$result = [ordered]@{}
try {
  $w = Wait-For { $p.Refresh(); if ($p.MainWindowHandle -ne 0) { $win = $A::FromHandle($p.MainWindowHandle); if (ById $win 'programs.search') { $win } } } 40000 'tela de programas'
  Start-Sleep 2
  Shot '1-lista.png' 1360
  (ById $w 'programs.search').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('ShiftQA')
  $item = Wait-For { (ByName $w $name) | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem } | Select-Object -First 1 } 10000 'programa falso na lista'
  $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 500
  Shot '2-busca.png' 1360
  $result.encontradoNaBusca = $true
  Invoke-El (ById $w 'programs.uninstall')
  $confirm = Wait-For { Dialog-Button $labels.'programs.uninstall' } 8000 'diálogo de confirmação'
  Shot '3-confirmar.png' 1360
  Invoke-El $confirm
  Start-Sleep 1
  Shot '4-desinstalando.png' 1360
  $remove = Wait-For { Dialog-Button $labels.'programs.leftovers.remove' } 30000 'diálogo de sobras'
  Shot '5-sobras.png' 1360
  Invoke-El $remove
  Start-Sleep 4
  Shot '6-final.png' 1360
} finally {
  if (-not $p.HasExited) { $p.CloseMainWindow() | Out-Null; Start-Sleep 2; if (-not $p.HasExited) { $p.Kill() } }
  $result.entradaRemovida = -not (Test-Path $uninstallKey)
  $result.pastaRemovida = -not (Test-Path $leftDir)
  $result.chaveRemovida = -not (Test-Path $leftKey)
  $bk = Get-ChildItem (Join-Path $data 'uninstall-backups') -Recurse -Filter *.reg -ErrorAction SilentlyContinue
  $result.backupReg = ($bk | Select-Object -First 1).FullName
  $result.backupTemValor = [bool]($bk -and (Get-Content $bk[0].FullName -Raw) -match '"Theme"="dark"')
  # Limpeza do que tiver sobrado (o teste nunca deixa lixo no registro)
  Remove-Item $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $leftKey -Recurse -Force -ErrorAction SilentlyContinue
  Remove-Item $leftDir -Recurse -Force -ErrorAction SilentlyContinue
  $result.GetEnumerator() | ForEach-Object { "{0} = {1}" -f $_.Key, $_.Value }
}
