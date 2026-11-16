param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$testRoot=Join-Path $projectRoot ('artifacts\update-tests\'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$destination=Join-Path $testRoot 'Portable With Spaces'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$registry='HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ShiftControl'
$before=if (Test-Path $registry) { Get-ItemProperty $registry | ConvertTo-Json -Depth 3 } else { '' }
function Install([bool]$update,[int]$callerPid=0) {
    $extra=if ($update) { '/UPDATE /NORESTART ' } else { '' }
    $wait=if ($callerPid -gt 0) { "/WAITPID=$callerPid " } else { '' }
    $process=Start-Process -FilePath (Resolve-Path $Installer).Path -ArgumentList ('/S /PORTABLE '+$extra+$wait+'/D='+$destination) -WindowStyle Hidden -PassThru
    if ($callerPid -gt 0) {
        Start-Sleep -Milliseconds 500
        if ($process.HasExited) { throw 'Installer did not wait for caller.' }
        if ([IO.File]::ReadAllText($assembly) -ne 'old test version') { throw 'Installer wrote files before caller exited.' }
    }
    if (-not $process.WaitForExit(120000)) { throw 'Installer timed out.' }
    if ($process.ExitCode -ne 0) { throw "Installer exit: $($process.ExitCode)" }
}
Install $false
$executable=Join-Path $destination 'ShiftControl.exe'
if (-not (Test-Path $executable)) { throw 'Executable missing.' }
$assembly=Join-Path $destination 'ShiftControl.dll'
$hash=(Get-FileHash $assembly -Algorithm SHA256).Hash
# Simulates a stale file being replaced; only the isolated test copy is changed.
[IO.File]::WriteAllText($assembly,'old test version')
$marker=Join-Path $destination 'preserved-user-file.txt'
[IO.File]::WriteAllText($marker,'preserve me')
$caller=Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\ping.exe') -ArgumentList @('-n','8','127.0.0.1') -WindowStyle Hidden -PassThru
try { Install $true $caller.Id }
finally { if (-not $caller.HasExited) { Stop-Process -Id $caller.Id } }
if ((Get-FileHash $assembly -Algorithm SHA256).Hash -ne $hash) { throw 'Update did not replace old binaries.' }
if ([IO.File]::ReadAllText($marker) -ne 'preserve me') { throw 'Unrelated portable file was lost.' }
if (Test-Path (Join-Path $destination 'Uninstall.exe')) { throw 'Portable update registered an uninstaller.' }
$after=if (Test-Path $registry) { Get-ItemProperty $registry | ConvertTo-Json -Depth 3 } else { '' }
if ($before -ne $after) { throw 'User installation registry changed.' }
$result=[pscustomobject]@{Passed=$true;SilentInstall=$true;WaitForCaller=$true;ReplacedBinaries=$true;PathWithSpaces=$true;PortablePreserved=$true;UserRegistryUnchanged=$true;RestartSuppressedForTest=$true;Directory=$destination}
$result | ConvertTo-Json | Set-Content (Join-Path $testRoot 'report.json') -Encoding UTF8
$result
