# Builds dist\ManaBoxCompanion.exe using the C# compiler that ships with Windows.
# No Visual Studio or .NET SDK needed:   powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fw   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc  = Join-Path $fw 'csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc (the .NET Framework 4.x that ships with Windows 10/11 is required)." }

$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null
$exe = Join-Path $dist 'ManaBoxCompanion.exe'

$refs = @(
    'System.dll', 'System.Core.dll', 'System.Xaml.dll', 'System.Net.Http.dll', 'System.Web.Extensions.dll',
    'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll',
    'WPF\System.Printing.dll', 'WPF\ReachFramework.dll'
) | ForEach-Object { '/reference:' + (Join-Path $fw $_) }

$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs | ForEach-Object { $_.FullName }

$cscArgs = @(
    '/nologo', '/target:winexe', '/optimize+', '/platform:anycpu', '/utf8output', '/warn:3',
    "/out:$exe",
    "/win32icon:$(Join-Path $root 'assets\app.ico')",
    "/win32manifest:$(Join-Path $root 'src\app.manifest')",
    "/resource:$(Join-Path $root 'src\Theme.xaml'),Theme.xaml",
    "/resource:$(Join-Path $root 'src\Main.xaml'),Main.xaml",
    "/resource:$(Join-Path $root 'assets\app.ico'),app.ico",
    "/resource:$(Join-Path $root 'sample\sample-collection.csv'),sample.csv"
) + $refs + $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed (csc exit code $LASTEXITCODE)." }
$size = [math]::Round((Get-Item $exe).Length / 1KB)
Write-Host "Built $exe ($size KB)"
