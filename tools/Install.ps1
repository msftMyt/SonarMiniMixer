$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $project 'artifacts\publish'
$install = Join-Path $env:LOCALAPPDATA 'Programs\SonarMiniMixer'

if (-not (Test-Path (Join-Path $publish 'SonarMiniMixer.exe'))) {
    throw 'Publish artifacts are missing. Run the release build first.'
}

New-Item -ItemType Directory -Path $install -Force | Out-Null
Copy-Item (Join-Path $publish 'SonarMiniMixer.exe') $install -Force
Copy-Item (Join-Path $publish 'SonarMiniMixer.Cli.exe') $install -Force

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Sonar Mini Mixer.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($startMenu)
$shortcut.TargetPath = Join-Path $install 'SonarMiniMixer.exe'
$shortcut.WorkingDirectory = $install
$shortcut.Description = 'Compact SteelSeries Sonar mixer'
$shortcut.Save()

Write-Output "Installed to $install"
Write-Output "Start Menu shortcut: $startMenu"
