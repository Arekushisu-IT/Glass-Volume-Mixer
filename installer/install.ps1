# Installs Glass Mixer for the current Windows user. No admin rights needed.
#   - copies the app to %LOCALAPPDATA%\Programs\Glass Mixer
#   - adds a Start menu shortcut
#   - lists it in Settings > Apps, with an Uninstall button
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'app'
$target = Join-Path $env:LOCALAPPDATA 'Programs\Glass Mixer'
$exe = Join-Path $target 'GlassMixer.exe'

Write-Host "Installing Glass Mixer to $target"

# Glass Mixer draws its window with WebView2, which ships with Windows 11 and recent Windows 10.
$webView2 = 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
if (-not (Test-Path $webView2) -and -not (Test-Path 'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}')) {
    Write-Warning 'The Microsoft Edge WebView2 Runtime was not found. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and run this again.'
}

# Close a running copy so its files can be replaced.
Get-Process GlassMixer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
Copy-Item $source $target -Recurse
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $target

# Start menu shortcut.
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Glass Mixer.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $target
$link.IconLocation = "$exe,0"
$link.Description = 'Per-app volume mixer'
$link.Save()

# Desktop shortcut, the same as the Start menu one.
Copy-Item $shortcut (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Glass Mixer.lnk') -Force

# Settings > Apps entry. HKCU = this user only.
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GlassMixer'
New-Item $key -Force | Out-Null
$sizeKb = [int]((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
$values = @{
    DisplayName     = 'Glass Mixer'
    DisplayVersion  = (Get-Item $exe).VersionInfo.ProductVersion.Split('+')[0]   # drop the "+commit" build suffix
    Publisher       = 'Arekushisu-IT'
    DisplayIcon     = $exe
    InstallLocation = $target
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$target\uninstall.ps1`""
}
foreach ($name in $values.Keys) { Set-ItemProperty $key $name $values[$name] }
Set-ItemProperty $key EstimatedSize $sizeKb -Type DWord
Set-ItemProperty $key NoModify 1 -Type DWord
Set-ItemProperty $key NoRepair 1 -Type DWord

# Start with Windows: on, and always pointing at this installed copy (an older entry may
# point at a build folder that gets deleted). Turn it off from the tray icon's menu.
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty $run GlassMixer "`"$exe`" --startup"

Write-Host 'Installed. Starting Glass Mixer...'
Start-Process $exe
