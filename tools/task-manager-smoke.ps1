param([string]$Executable,[switch]$Narrow)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable=Join-Path $projectRoot 'src\ShiftControl.App\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\ShiftControl.exe' }
$testRoot=Join-Path $projectRoot ('artifacts\task-manager-tests\'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$labels=Get-Content (Join-Path $projectRoot 'src\ShiftControl.App\Strings\pt-BR.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class TaskUiNative {
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
 [DllImport("user32.dll")] public static extern void keybd_event(byte key,byte scan,uint flags,UIntPtr extra);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int cmd);
}
'@
$helper=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\ping.exe') -ArgumentList @('-n','120','127.0.0.1') -WindowStyle Hidden -PassThru
$process=$null
try {
    $process=Start-Process -FilePath $Executable -ArgumentList ('--page=tasks --data-dir="'+(Join-Path $testRoot 'data')+'"') -WindowStyle Hidden -PassThru
    for ($i=0;$i -lt 60;$i++) { Start-Sleep -Milliseconds 300; $process.Refresh(); if ($process.MainWindowHandle -ne 0) { break } }
    if ($process.MainWindowHandle -eq 0) { throw 'Janela não encontrada.' }
    $window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    [TaskUiNative]::ShowWindow($process.MainWindowHandle,9) | Out-Null
    $width=if ($Narrow) {800} else {1360}
    [TaskUiNative]::SetWindowPos($process.MainWindowHandle,[IntPtr]::Zero,30,30,$width,880,0x0040) | Out-Null
    function By-Id([string]$id) {
        for ($attempt=0;$attempt -lt 15;$attempt++) {
            $element=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)))
            if ($element) { return $element }
            Start-Sleep -Milliseconds 100
        }
    }
    function Click($element) {
        if (-not $element) { throw 'Controle não encontrado.' }
        $element.SetFocus();Start-Sleep -Milliseconds 100
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    function Search([string]$value) {
        (By-Id 'tasks.search').GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($value)
        Start-Sleep -Milliseconds 350
    }
    function Select-Pid([int]$targetPid) {
        Search ([string]$targetPid)
        $text=$null
        for ($i=0;$i -lt 20;$i++) {
            $text=(By-Id 'tasks.list').FindFirst([System.Windows.Automation.TreeScope]::Descendants,
                (New-Object System.Windows.Automation.AndCondition(
                    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,[string]$targetPid)),
                    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text)))))
            if ($text) { break };Start-Sleep -Milliseconds 250
        }
        if (-not $text) { throw "PID $targetPid não encontrado." }
        $parent=$text
        for ($i=0;$i -lt 10;$i++) {
            if ($parent.Current.ControlType -eq [System.Windows.Automation.ControlType]::ListItem) {
                $parent.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
                $parent.SetFocus()
                Start-Sleep -Milliseconds 200
                return
            }
            $parent=[System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($parent)
        }
        throw 'Linha do processo não encontrada.'
    }
    function Dialog-Button([string]$id,[string]$name) {
        $buttons=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)))
        @($buttons | Where-Object { $_.Current.Name -eq $name -and -not $_.Current.IsOffscreen }) | Select-Object -First 1
    }
    function Assert-OwnHelperConfirmation {
        # Somente o processo criado por este script pode ser encerrado pelo teste.
        $expected=[string]::Format($labels.'tasks.endConfirm',($helper.ProcessName+'.exe'),$helper.Id)
        $text=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$expected)))
        if (-not $text) { throw 'A confirmação não corresponde ao processo auxiliar do teste. Encerramento bloqueado.' }
    }
    function Capture([string]$name) {
        $rect=$window.Current.BoundingRectangle
        if ($rect.IsEmpty -or $rect.Width -le 0 -or $rect.Height -le 0) { return }
        $bitmap=New-Object System.Drawing.Bitmap([int]$rect.Width,[int]$rect.Height)
        $graphics=[System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $dc=$graphics.GetHdc()
            try { [TaskUiNative]::PrintWindow($process.MainWindowHandle,$dc,2) | Out-Null } finally { $graphics.ReleaseHdc($dc) }
            $bitmap.Save((Join-Path $testRoot $name))
        } finally { $graphics.Dispose();$bitmap.Dispose() }
    }
    for ($i=0;$i -lt 50;$i++) { $updated=By-Id 'tasks.updated'; if ($updated -and $updated.Current.Name) { break };Start-Sleep -Milliseconds 200 }
    Start-Sleep -Seconds 3
    Capture 'initial.png'
    $combo=By-Id 'tasks.sort'
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 150
    $options=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$labels.'tasks.sort.Memory')))))
    $sorted=$false
    foreach ($option in $options) {
        $pattern=$null
        if ($option.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$pattern)) { $pattern.Select();$sorted=$true;break }
    }
    if (-not $sorted) { throw 'Ordenação por memória não foi selecionada.' }
    try { $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch {}
    (By-Id 'tasks.appsOnly').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 300
    $background=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$labels.'tasks.state.background')))
    if (@($background | Where-Object { -not $_.Current.IsOffscreen }).Count -gt 0) { throw 'Filtro de aplicativos incluiu processo em segundo plano.' }
    (By-Id 'tasks.appsOnly').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 300
    Capture 'processes.png'
    (By-Id 'tasks.pause').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Milliseconds 200
    $before=(By-Id 'tasks.updated').Current.Name
    Start-Sleep -Seconds 3
    if ($before -ne (By-Id 'tasks.updated').Current.Name) { throw 'A pausa não interrompeu as atualizações.' }
    Search 'shift-task-test-no-match-643219'
    $empty=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$labels.'tasks.empty')))
    if (-not $empty -or $empty.Current.IsOffscreen) { throw 'Busca sem resultado não funcionou.' }
    Select-Pid $process.Id
    if ((By-Id 'tasks.end').Current.IsEnabled) { throw 'O próprio aplicativo deveria estar protegido.' }
    Select-Pid $helper.Id
    if (-not (By-Id 'tasks.end').Current.IsEnabled) { throw 'O auxiliar deveria poder ser encerrado.' }
    Click (By-Id 'tasks.end');Start-Sleep -Milliseconds 300
    Assert-OwnHelperConfirmation
    Click (Dialog-Button 'CloseButton' $labels.'common.cancel');Start-Sleep -Milliseconds 300
    $helper.Refresh();if ($helper.HasExited) { throw 'Cancelar encerrou o auxiliar.' }
    # Exercita o menu de contexto com Shift+F10, na linha do auxiliar.
    function Send-Key([byte]$key,[bool]$shift=$false) {
        [TaskUiNative]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 150
        if ([TaskUiNative]::GetForegroundWindow() -ne $process.MainWindowHandle) { throw 'Janela de teste sem foco; teclado bloqueado.' }
        if ($shift) { [TaskUiNative]::keybd_event(0x10,0,0,[UIntPtr]::Zero) }
        [TaskUiNative]::keybd_event($key,0,0,[UIntPtr]::Zero)
        [TaskUiNative]::keybd_event($key,0,2,[UIntPtr]::Zero)
        if ($shift) { [TaskUiNative]::keybd_event(0x10,0,2,[UIntPtr]::Zero) }
        Start-Sleep -Milliseconds 400
    }
    Select-Pid $helper.Id
    Send-Key 0x79 $true
    $menuItem=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$labels.'tasks.end')),
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::MenuItem)))))
    Click $menuItem
    Start-Sleep -Milliseconds 300
    Assert-OwnHelperConfirmation
    Click (Dialog-Button 'CloseButton' $labels.'common.cancel')
    Start-Sleep -Milliseconds 300
    Select-Pid $helper.Id
    Send-Key 0x2E
    Assert-OwnHelperConfirmation
    Click (Dialog-Button 'CloseButton' $labels.'common.cancel')
    Start-Sleep -Milliseconds 300
    $helper.Refresh();if ($helper.HasExited) { throw 'Cancelar atalhos encerrou o auxiliar.' }
    Click (By-Id 'tasks.details');Start-Sleep -Milliseconds 300
    Capture 'details.png'
    Search ([string]$helper.Id) # Tira foco do flyout; não altera o processo selecionado.
    (By-Id 'tasks.search').SetFocus();Start-Sleep -Milliseconds 300
    Click (By-Id 'tasks.end');Start-Sleep -Milliseconds 300
    Assert-OwnHelperConfirmation
    Click (Dialog-Button 'PrimaryButton' $labels.'tasks.end')
    if (-not $helper.WaitForExit(5000)) { throw 'O auxiliar não encerrou.' }
    Search ''
    (By-Id 'tasks.pause').GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    Start-Sleep -Seconds 3
    Capture 'completed.png'
    $result=[pscustomobject]@{Passed=$true;Narrow=[bool]$Narrow;Search=$true;Paused=$true;SelfProtected=$true;CancelEnd=$true;ContextMenu=$true;DeleteShortcut=$true;HelperEnded=$true;HelperPid=$helper.Id;DataDirectory=(Join-Path $testRoot 'data')}
    $result | ConvertTo-Json | Set-Content (Join-Path $testRoot 'report.json') -Encoding UTF8
    $result
} catch {
    $_ | Out-String | Set-Content (Join-Path $testRoot 'failure.txt') -Encoding UTF8
    if ($window) {
        Capture 'failed.png'
        $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition) |
            Where-Object { $_.Current.AutomationId } | ForEach-Object { [pscustomobject]@{Id=$_.Current.AutomationId;Name=$_.Current.Name;Class=$_.Current.ClassName} } |
            ConvertTo-Json -Depth 3 | Set-Content (Join-Path $testRoot 'failed-controls.json') -Encoding UTF8
    }
    throw
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id }
    if (-not $helper.HasExited) { Stop-Process -Id $helper.Id }
}
