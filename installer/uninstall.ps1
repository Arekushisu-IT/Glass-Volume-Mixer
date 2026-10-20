# Removes Glass Mixer for the current user. Run from Settings > Apps > Glass Mixer > Uninstall.
# Your settings in %LOCALAPPDATA%\GlassMixer are kept, in case you install it again.
$target = Join-Path $env:LOCALAPPDATA 'Programs\Glass Mixer'

Get-Process GlassMixer -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' GlassMixer -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Glass Mixer.lnk') -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\GlassMixer' -Recurse -ErrorAction SilentlyContinue

# This script lives inside the folder it deletes, so the folder is renamed first and a
# separate process removes the renamed copy a moment later. Renaming first means a
# reinstall straight after this can't be deleted by that delayed clean-up.
$doomed = "$target.removing-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
try { Rename-Item $target (Split-Path $doomed -Leaf) -ErrorAction Stop } catch { $doomed = $target }
Start-Process cmd.exe -ArgumentList "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q `"$doomed`"" -WindowStyle Hidden
Write-Host 'Glass Mixer was removed.'
