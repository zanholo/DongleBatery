# Gera dist\MonitorBateria.exe (arquivo único; requer o .NET 10 Desktop Runtime instalado).
# Use -SelfContained para um exe que roda em qualquer Windows 10/11 sem instalar nada (~150 MB).
param([switch]$SelfContained)
$sc = if ($SelfContained) { "true" } else { "false" }
Get-Process MonitorBateria -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish "$PSScriptRoot\src\MonitorBateria" -c Release -r win-x64 --self-contained $sc `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
  -o "$PSScriptRoot\dist"
