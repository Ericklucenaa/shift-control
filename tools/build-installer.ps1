param([string]$MakeNsis, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactPath = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $projectRoot 'artifacts' }
$publishPath = Join-Path $artifactPath 'publish'
[xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props')
$appVersion = [string]$props.Project.PropertyGroup.Version
if (-not $MakeNsis) {
    $candidates = @((Join-Path $env:LOCALAPPDATA 'tauri\NSIS\makensis.exe'), (Join-Path $env:LOCALAPPDATA 'tauri\NSIS\Bin\makensis.exe'))
    $MakeNsis = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (-not $MakeNsis) { throw 'Informe -MakeNsis com o caminho do makensis.exe.' }
New-Item -ItemType Directory -Path $artifactPath -Force | Out-Null
dotnet publish (Join-Path $projectRoot 'src\ShiftControl.App\ShiftControl.App.csproj') -c Release -r win-x64 --self-contained true -p:Platform=x64 -o $publishPath
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar.' }
$installerPath = Join-Path $artifactPath "ShiftControl-$appVersion-setup.exe"
& $MakeNsis "/DAPP_VERSION=$appVersion" "/DPUBLISH_DIR=$publishPath" "/DOUTPUT_FILE=$installerPath" (Join-Path $projectRoot 'installer\ShiftControl.nsi')
if ($LASTEXITCODE -ne 0) { throw 'Falha ao gerar instalador.' }
$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $installerPath)" | Set-Content -LiteralPath "$installerPath.sha256" -Encoding ASCII
Get-Item -LiteralPath $installerPath
