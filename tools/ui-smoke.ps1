param([string]$Executable = '', [switch]$Narrow, [switch]$Exercise)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $projectRoot 'artifacts\publish\ShiftControl.exe' }
$testData = Join-Path $projectRoot ('artifacts\ui-test-data\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$screenshots = Join-Path $projectRoot 'artifacts\screenshots'
New-Item -ItemType Directory -Path $testData,$screenshots -Force | Out-Null
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ShiftUiNative {
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
 [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr p);
 [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder name, int size);
 private delegate bool EnumProc(IntPtr h, IntPtr p);
 public static IntPtr TrayWindow(int pid) {
   IntPtr found = IntPtr.Zero;
   EnumWindows((h,p) => {
     uint owner; GetWindowThreadProcessId(h, out owner);
     var name = new System.Text.StringBuilder(256); GetClassName(h, name, 256);
     if (owner == pid && name.ToString() == "ShiftControl.Tray") { found = h; return false; }
     return true;
   }, IntPtr.Zero);
   return found;
 }
}
'@
$process = Start-Process -FilePath $Executable -ArgumentList ('--data-dir="' + $testData + '"') -WorkingDirectory (Split-Path $Executable) -WindowStyle Hidden -PassThru
try {
    $window = $null
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) { throw 'O app encerrou durante a inicialização.' }
        if ($process.MainWindowHandle -ne 0) {
            $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            break
        }
    }
    if (-not $window) { throw 'Janela principal não encontrada.' }
    $width = if ($Narrow) { 800 } else { 1360 }
    [ShiftUiNative]::SetWindowPos($process.MainWindowHandle, [IntPtr]::Zero, 40, 40, $width, 880, 0x0040) | Out-Null
    [ShiftUiNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    Start-Sleep -Seconds 2
    $labels = Get-Content -LiteralPath (Join-Path $projectRoot 'src\ShiftControl.App\Strings\pt-BR.json') -Encoding UTF8 -Raw | ConvertFrom-Json
    $keys = @('overview','history','sensors','drivers','disks','overlay','sessions','boost','formatKit','checks','migration','cleanup','startup','repair','network','alerts','undo','settings')
    $pages = @($keys | ForEach-Object { $labels."nav.$_" })
    $report = @()
    function Invoke-NamedButton([string]$name) {
        $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))))
        if (-not $button) { throw "Botão não encontrado: $name" }
        $button.SetFocus()
        Start-Sleep -Milliseconds 150
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    foreach ($name in $pages) {
        $matches = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
        $selected = $false
        foreach ($item in $matches) {
            $pattern = $null
            if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
                $pattern.Select(); $selected = $true; break
            }
            if ($item.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
                $pattern.Invoke(); $selected = $true; break
            }
        }
        if (-not $selected) {
            $report += [pscustomobject]@{ Page = $name; Navigated = $false; VisibleText = @() }
            continue
        }
        Start-Sleep -Seconds 2
        $action = ''
        if ($Exercise -and ($name -eq $labels.'nav.network' -or $name -eq $labels.'nav.boost')) {
            $edits = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
            foreach ($edit in $edits) {
                $value = $null
                if ($edit.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$value) -and -not $value.Current.IsReadOnly) {
                    if ($name -eq $labels.'nav.network') {
                        $value.SetValue('127.0.0.1')
                        Invoke-NamedButton $labels.'network.run'
                        Start-Sleep -Seconds 13
                        $action = 'Teste ICMP de loopback executado pela interface.'
                    } else {
                        $value.SetValue('shift-ui-test-game.exe')
                        Invoke-NamedButton $labels.'boost.addGame'
                        Start-Sleep -Milliseconds 300
                        $profile = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                            (New-Object System.Windows.Automation.AndCondition(
                                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'shift-ui-test-game.exe')),
                                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))))
                        if (-not $profile) { throw 'O perfil não apareceu na lista após adicionar o executável.' }
                        $action = 'Perfil por executável adicionado sem ativar boost.'
                    }
                    break
                }
            }
        }
        if ($Exercise -and $name -eq $labels.'nav.settings') {
            Invoke-NamedButton $labels.'update.check'
            Start-Sleep -Seconds 3
            $action = 'Consulta de release acionada pela interface.'
        }
        $texts = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
        $visible = @($texts | Where-Object { -not $_.Current.IsOffscreen } | ForEach-Object { $_.Current.Name })
        $rect = $window.Current.BoundingRectangle
        $bitmap = New-Object System.Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc = $graphics.GetHdc()
            try { [ShiftUiNative]::PrintWindow($process.MainWindowHandle, $dc, 2) | Out-Null }
            finally { $graphics.ReleaseHdc($dc) }
            $slug = ($name -replace '[^\w]+','-')
            $suffix = if ($Narrow) { '-narrow' } else { '' }
            $bitmap.Save((Join-Path $screenshots ($slug + $suffix + '.png')))
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
        $report += [pscustomobject]@{ Page = $name; Navigated = $true; Action = $action; DataDirectory = $testData; VisibleText = $visible }
    }
    if ($Exercise) {
        $tray = [ShiftUiNative]::TrayWindow($process.Id)
        if ($tray -eq [IntPtr]::Zero) { throw 'Janela da bandeja não encontrada.' }
        [ShiftUiNative]::PostMessage($tray, 0x8015, [IntPtr](800 -bor (700 -shl 16)), [IntPtr]0x400) | Out-Null
        Start-Sleep -Seconds 1
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)))
        $panel = @($windows | Where-Object { $_.Current.BoundingRectangle.Width -gt 250 -and $_.Current.BoundingRectangle.Width -lt 600 }) | Select-Object -First 1
        if (-not $panel) { throw 'Mini-painel não abriu.' }
        [ShiftUiNative]::PostMessage([IntPtr]$panel.Current.NativeWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        [ShiftUiNative]::PostMessage($process.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
        Start-Sleep -Milliseconds 500
        if ([ShiftUiNative]::IsWindowVisible($process.MainWindowHandle)) { throw 'Fechar para a bandeja não ocultou a janela.' }
        [ShiftUiNative]::PostMessage($tray, 0x8015, [IntPtr]::Zero, [IntPtr]0x405) | Out-Null
        Start-Sleep -Milliseconds 500
        if (-not [ShiftUiNative]::IsWindowVisible($process.MainWindowHandle)) { throw 'Não foi possível restaurar a janela pela bandeja.' }
        $report += [pscustomobject]@{ Page = 'Bandeja e mini-painel'; Navigated = $true; Action = 'Mini-painel aberto; janela principal ocultada e restaurada.'; DataDirectory = $testData; VisibleText = @() }
    }
    $suffix = if ($Narrow) { '-narrow' } else { '' }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $projectRoot ('artifacts\ui-report' + $suffix + '.json')) -Encoding UTF8
    $report | Select-Object Page,Navigated
} finally {
    if (-not $process.HasExited) {
        # Somente a instância criada pelo teste, com banco isolado; não toca no app do usuário.
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
        $closeToTray = $false
        # WM_CLOSE pode mandar à bandeja; o processo pertence ao teste e tem estado descartável.
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
    }
}
