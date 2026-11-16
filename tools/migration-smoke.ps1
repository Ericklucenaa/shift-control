param([string]$Executable)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $Executable) { $Executable = Join-Path $projectRoot 'src\ShiftControl.App\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\ShiftControl.exe' }
$testRoot = Join-Path $projectRoot ('artifacts\migration-tests\' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$destination = Join-Path $testRoot 'destination'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$labels = Get-Content (Join-Path $projectRoot 'src\ShiftControl.App\Strings\pt-BR.json') -Raw -Encoding UTF8 | ConvertFrom-Json
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class MigrationNative {
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint message, IntPtr w, IntPtr l);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint message, IntPtr w, string value);
}
'@
$process = Start-Process -FilePath $Executable -ArgumentList ('--page=migration --data-dir="' + (Join-Path $testRoot 'data') + '"') -WindowStyle Hidden -PassThru
try {
    for ($i=0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 300; $process.Refresh(); if ($process.MainWindowHandle -ne 0) { break } }
    if ($process.MainWindowHandle -eq 0) { throw 'Janela de teste não encontrada.' }
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
    Start-Sleep -Milliseconds 500
    function By-Id($scope, [string]$id) {
        $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)))
    }
    function Dialog-Button($scope, [string]$id) {
        $scope.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.AndCondition(
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)),
                (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty,'Button')))))
    }
    function Click($element) {
        if (-not $element) { throw 'Controle não encontrado.' }
        if ($element.Current.ClassName -eq 'Button' -and $element.Current.NativeWindowHandle -ne 0) {
            [MigrationNative]::PostMessage([IntPtr]$element.Current.NativeWindowHandle,0xF5,[IntPtr]::Zero,[IntPtr]::Zero) | Out-Null
            return
        }
        $element.SetFocus()
        Start-Sleep -Milliseconds 100
        $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    function Set-Checked([string]$id, [bool]$on) {
        $element = By-Id $window $id
        $toggle = $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        if (($toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) -ne $on) { $toggle.Toggle() }
        Start-Sleep -Milliseconds 150
    }
    function Find-FolderDialog {
        $nested = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ClassNameProperty,'#32770')))
        foreach ($dialog in $nested) { if ((Dialog-Button $dialog '1') -and (Dialog-Button $dialog '2')) { return $dialog } }
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)))
        @($windows | Where-Object { $_.Current.NativeWindowHandle -ne $process.MainWindowHandle -and (Dialog-Button $_ '1') -and (Dialog-Button $_ '2') }) | Select-Object -First 1
    }
    function Wait-FolderDialog {
        for ($i=0; $i -lt 40; $i++) {
            $dialog = Find-FolderDialog
            if ($dialog) { return $dialog }
            Start-Sleep -Milliseconds 250
        }
        $diagnostics = @()
        $roots = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$process.Id)))
        foreach ($root in $roots) {
            $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
            $diagnostics += [pscustomobject]@{ Root=$root.Current.Name; Class=$root.Current.ClassName; Id=$root.Current.AutomationId; Elements=@($all | ForEach-Object { [pscustomobject]@{ Name=$_.Current.Name; Id=$_.Current.AutomationId; Class=$_.Current.ClassName; Type=$_.Current.ControlType.ProgrammaticName } }) }
        }
        $diagnostics | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $testRoot 'picker-diagnostics.json') -Encoding UTF8
        throw 'O teste não encontrou o seletor de pasta. Consulte picker-diagnostics.json.'
    }
    function Choose-Folder([string]$path) {
        $dialog = Wait-FolderDialog
        $inputBox = By-Id $dialog '1152'
        if (-not $inputBox) { $inputBox = By-Id $dialog '1148' }
        if (-not $inputBox) { throw 'Campo de pasta do diálogo não encontrado.' }
        $value = $null
        if ($inputBox.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern,[ref]$value)) { $value.SetValue($path) }
        else { [MigrationNative]::SendMessage([IntPtr]$inputBox.Current.NativeWindowHandle,0xC,[IntPtr]::Zero,$path) | Out-Null }
        for ($i=0; $i -lt 3; $i++) {
            Click (Dialog-Button $dialog '1')
            Start-Sleep -Milliseconds 300
            if (-not (Find-FolderDialog)) { return }
            $dialog = Find-FolderDialog
        }
        throw 'O seletor não confirmou a pasta.'
    }
    function Packages { @(Get-ChildItem -LiteralPath $destination -Filter 'shift-control-migracao.json' -File -Recurse) }
    Set-Checked 'migration.programs' $false
    Set-Checked 'migration.drivers' $false
    Set-Checked 'migration.wifi' $false
    Set-Checked 'migration.settings' $false
    Click (By-Id $window 'migration.create')
    Start-Sleep -Milliseconds 300
    $message = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$labels.'migration.chooseOptions')))
    if (-not $message) { throw 'Nenhuma opção: mensagem não apareceu.' }
    if (Find-FolderDialog) { throw 'Não deveria abrir seletor sem opções.' }
    Set-Checked 'migration.settings' $true
    Click (By-Id $window 'migration.create')
    $dialog = Wait-FolderDialog
    Click (Dialog-Button $dialog '2')
    Start-Sleep -Milliseconds 300
    if ((Packages).Count -ne 0) { throw 'Cancelar criou arquivos inesperados.' }
    $created = @()
    for ($n=1; $n -le 2; $n++) {
        Click (By-Id $window 'migration.create')
        Choose-Folder $destination
        for ($i=0; $i -lt 40 -and (Packages).Count -lt $n; $i++) { Start-Sleep -Milliseconds 200 }
        if ((Packages).Count -ne $n) { throw "Pacote $n não foi gravado em pasta independente." }
    }
    foreach ($manifestFile in (Packages)) {
        $manifest = Get-Content $manifestFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $manifest.AppSettings -or $manifest.Version -ne 1) { throw 'Manifesto inválido.' }
        $settingsFile = Join-Path $manifestFile.DirectoryName 'preferencias.json'
        $settings = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $settings.app -or -not $settings.overlay -or -not $settings.gameBoost -or -not $settings.alerts) { throw 'Preferências incompletas.' }
        $created += $manifestFile.DirectoryName
    }
    Click (By-Id $window 'migration.open')
    Choose-Folder $created[0]
    Start-Sleep -Milliseconds 400
    $restoreButton = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$labels.'migration.restoreAll')))
    if (-not $restoreButton) { throw 'Pacote criado não foi reconhecido ao abrir.' }
    # Exercita a confirmação de Wi-Fi somente com cancelamento; nunca exporta perfis ou senhas reais.
    Set-Checked 'migration.wifi' $true
    Click (By-Id $window 'migration.create')
    Choose-Folder $destination
    Start-Sleep -Milliseconds 400
    $buttons = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty,$labels.'common.cancel')))
    $cancel = @($buttons | Where-Object { $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -and -not $_.Current.IsOffscreen }) | Select-Object -First 1
    if (-not $cancel) { throw 'Confirmação de Wi-Fi não apareceu.' }
    Click $cancel
    if ((Packages).Count -ne 2) { throw 'Cancelar confirmação de Wi-Fi criou outro pacote.' }
    $result = [pscustomobject]@{ Passed=$true; NoOptions=$true; PickerCancel=$true; CreatedPackages=$created; OpenCreatedPackage=$true; WifiConsentCancel=$true; DataDirectory=(Join-Path $testRoot 'data') }
    $result | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $testRoot 'report.json') -Encoding UTF8
    $result
} finally {
    if (-not $process.HasExited) { Stop-Process -Id $process.Id }
}
