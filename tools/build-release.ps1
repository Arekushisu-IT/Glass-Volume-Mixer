# Builds an installable release of Glass Mixer:
#   dist/GlassMixer-<version>-win-x64/        the folder to share
#     app/                                     the program (self-contained: no .NET install needed)
#     Install Glass Mixer.cmd                  double-click to install
#     install.ps1, uninstall.ps1
#   dist/GlassMixer-<version>-win-x64.zip      the same, zipped
#
#   powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $root 'src\GlassMixer.App\GlassMixer.App.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "GlassMixer-$version-win-x64"
$out = Join-Path $root "dist\$name"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

# Self-contained: the .NET runtime is included, so the PC doesn't need .NET installed.
# Single file: one GlassMixer.exe (plus the ui folder and icon next to it).
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o (Join-Path $out 'app')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
# WebView2's NuGet package adds its API documentation (.xml); users don't need it.
Remove-Item (Join-Path $out 'app\*.xml')
foreach ($required in 'GlassMixer.exe', 'app.ico', 'ui\index.html', 'ui\fonts\fonts.css') {
    if (-not (Test-Path (Join-Path $out "app\$required"))) { throw "Release is missing $required" }
}

Copy-Item (Join-Path $root 'installer\install.ps1'), (Join-Path $root 'installer\uninstall.ps1'), (Join-Path $root 'installer\Install Glass Mixer.cmd') $out

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path "$out\*" -DestinationPath $zip
"Release ready:"
"  $out"
"  $zip  ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
