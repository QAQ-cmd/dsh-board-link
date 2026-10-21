param(
  [string]$Source = '',
  [string]$InstallDir = "$env:LOCALAPPDATA\DshLink\bin",
  [string]$ConfigPath = "$env:LOCALAPPDATA\DshLink\config.json",
  [switch]$NoAutostart,
  [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Source) { $Source = Join-Path $root 'dist\win-x64' }
if (-not (Test-Path (Join-Path $Source 'dsh-link.exe'))) {
  throw "dsh-link.exe not found under $Source. Run .\scripts\publish.ps1 first."
}

$InstallDir = [IO.Path]::GetFullPath($InstallDir)
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $Source 'dsh-link.exe') (Join-Path $InstallDir 'dsh-link.exe') -Force
$exe = (Resolve-Path (Join-Path $InstallDir 'dsh-link.exe')).Path
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ConfigPath) | Out-Null
if (-not (Test-Path $ConfigPath)) {
  & $exe init --config $ConfigPath
} else {
  Write-Host "Keeping existing config: $ConfigPath"
}

# .vbs launchers start the console-subsystem agent with a hidden window. The normal
# shortcut starts the agent only (no Edge/account popup); the Console shortcut opens
# the local dashboard when it is actually wanted.
function New-DshVbs([string]$path, [string]$arguments) {
  $bundle = Join-Path $env:LOCALAPPDATA 'DshLink\bundle'
  New-Item -ItemType Directory -Force -Path $bundle | Out-Null
  $run = '"' + $exe + '" ' + $arguments + ' --config "' + $ConfigPath + '"'
  $escapedRun = $run.Replace('"', '""')
  $vbs = 'Set sh = CreateObject("WScript.Shell")' + "`r`n" +
         'sh.Environment("PROCESS")("DOTNET_BUNDLE_EXTRACT_BASE_DIR") = "' + $bundle + '"' + "`r`n" +
         'sh.Run "' + $escapedRun + '", 0, False' + "`r`n"
  Set-Content -Path $path -Value $vbs -Encoding ascii
}
$agentVbs = Join-Path $InstallDir 'DSH Link.vbs'
New-DshVbs $agentVbs '--gui'

if (-not $NoShortcut) {
  try {
    $ws = New-Object -ComObject WScript.Shell
    $desktop = [Environment]::GetFolderPath('Desktop')
    $shortcuts = @(@{ Name = 'DSH Link.lnk'; Vbs = $agentVbs })
    foreach ($item in $shortcuts) {
      $shortcutPath = Join-Path $desktop $item.Name
      $shortcut = $ws.CreateShortcut($shortcutPath)
      $shortcut.TargetPath = Join-Path $env:WINDIR 'System32\wscript.exe'
      $shortcut.Arguments = "`"$($item.Vbs)`""
      $shortcut.WorkingDirectory = $InstallDir
      $shortcut.IconLocation = "$exe,0"
      $shortcut.Save()
      Write-Host "Desktop shortcut: $shortcutPath"
    }
  } catch {
    Write-Warning "Could not create desktop shortcut: $($_.Exception.Message)"
  }
}

if (-not $NoAutostart) {
  $taskName = 'DshLink'
  $command = "wscript.exe `"`"$agentVbs`"`""
  schtasks.exe /Create /TN $taskName /SC ONLOGON /RL LIMITED /F /TR $command | Out-Host
  Write-Host "Autostart task created: $taskName (runs at next logon)"
}

Write-Host ''
Write-Host "Installed: $exe"
Write-Host "Config:    $ConfigPath"
Write-Host 'Next: edit tunnel.ssh.target and identityFile, then run:'
Write-Host "  `"$exe`" doctor --config `"$ConfigPath`""
Write-Host "  `"$exe`" run --config `"$ConfigPath`""
Write-Host "  `"$exe`" ui --config `"$ConfigPath`"   (GUI dashboard)"
Write-Host "Agent launcher:   `"$agentVbs`"   (native window + tray)"
Write-Host "Agent token: `"$exe`" print-token --config `"$ConfigPath`""
