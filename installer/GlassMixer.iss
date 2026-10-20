; Optional: a classic setup.exe with Inno Setup (https://jrsoftware.org/isinfo.php).
; Build the release first (tools/build-release.ps1), then compile this file with Inno Setup.
; The PowerShell installer next to this file does the same job without extra tools.

#define AppVersion "1.0.0"

[Setup]
AppId={{6F3B9C52-2E1D-4C6A-9C0B-5B7E2A9D41F3}
AppName=Glass Mixer
AppVersion={#AppVersion}
AppPublisher=Arekushisu-IT
DefaultDirName={localappdata}\Programs\Glass Mixer
DefaultGroupName=Glass Mixer
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=GlassMixer-{#AppVersion}-setup
SetupIconFile=..\src\GlassMixer.App\app.ico
UninstallDisplayIcon={app}\GlassMixer.exe
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Files]
Source: "..\dist\GlassMixer-{#AppVersion}-win-x64\app\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{group}\Glass Mixer"; Filename: "{app}\GlassMixer.exe"

[Tasks]
Name: "startup"; Description: "Start with Windows"; Flags: unchecked

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "GlassMixer"; \
    ValueData: """{app}\GlassMixer.exe"" --startup"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\GlassMixer.exe"; Description: "Start Glass Mixer"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/im GlassMixer.exe /f"; Flags: runhidden; RunOnceId: "StopGlassMixer"
