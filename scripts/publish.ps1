param(
  [string]$Runtime = 'win-x64',
  [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$project = Join-Path $root 'src\DshLink.Agent\DshLink.Agent.csproj'
$output = Join-Path $root "dist\$Runtime"

$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
Write-Host "Publishing $project -> $output (self-contained=$selfContained, single exe)"
if (Test-Path $output) { Remove-Item $output -Recurse -Force }
dotnet publish $project `
  -c Release `
  -r $Runtime `
  --self-contained $selfContained `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:IncludeAllContentForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -o $output

# Keep the single-file executable; static-web-assets metadata and IIS web.config
# are not used by this local agent.
Get-ChildItem $output -File | Where-Object { $_.Name -ne 'dsh-link.exe' } | Remove-Item -Force

Write-Host ''
Write-Host "Published: $(Join-Path $output 'dsh-link.exe')"
Write-Host "Install with: .\scripts\install.ps1 -Source <published folder>"
