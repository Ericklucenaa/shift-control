# Baixa o Intel PresentMon (licença MIT) usado na medição de FPS e confere o SHA-256.
# Uso: powershell -File tools/fetch-presentmon.ps1
$ErrorActionPreference = "Stop"
$version = "2.6.0"
$sha256 = "b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af"
$dest = Join-Path $PSScriptRoot "..\src\ShiftControl.App\Tools\PresentMon.exe"
$url = "https://github.com/GameTechDev/PresentMon/releases/download/v$version/PresentMon-$version-x64.exe"
New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
if ((Test-Path $dest) -and ((Get-FileHash $dest -Algorithm SHA256).Hash -eq $sha256)) { "PresentMon $version já presente."; exit 0 }
$tmp = "$dest.download"
Invoke-WebRequest $url -OutFile $tmp -UseBasicParsing
$actual = (Get-FileHash $tmp -Algorithm SHA256).Hash
if ($actual -ne $sha256) { Remove-Item $tmp; throw "SHA-256 inesperado para PresentMon: $actual" }
Move-Item -Force $tmp $dest
"PresentMon $version instalado em $dest"
