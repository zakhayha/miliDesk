param(
  [string]$OutName = "dist"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path "$root\src\DeskMonitor.cs")) {
  throw "Run this script from the miliDesk repo (src\compile.ps1)."
}
$fw = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$wpf = Join-Path $fw "WPF"
$outDir = Join-Path $root $OutName
$lhm = Join-Path $root "lib\lhm"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Copy-Item (Join-Path $root "assets\icon.ico") (Join-Path $outDir "icon.ico") -Force

if (Test-Path $lhm) {
  Get-ChildItem $lhm -Filter "*.dll" | Where-Object {
    $_.Name -notmatch '^(Aga\.Controls|OxyPlot)'
  } | ForEach-Object {
    # A running instance keeps these loaded; they never change between builds.
    try { Copy-Item $_.FullName -Destination (Join-Path $outDir $_.Name) -Force } catch { }
  }
}

$csc = Join-Path $fw "csc.exe"
$sources = Get-ChildItem (Join-Path $root "src\*.cs") | ForEach-Object { $_.FullName }
$args = @(
  "/nologo",
  "/target:winexe",
  "/platform:x64",
  "/optimize+",
  "/out:$outDir\DeskMonitor.exe",
  "/win32icon:$outDir\icon.ico",
  "/win32manifest:$root\src\app.manifest",
  "/resource:$root\src\theme.xaml,DeskMonitor.theme.xaml",
  "/r:$wpf\PresentationFramework.dll",
  "/r:$wpf\PresentationCore.dll",
  "/r:$wpf\WindowsBase.dll",
  "/r:$fw\System.Xaml.dll",
  "/r:$fw\System.Windows.Forms.dll",
  "/r:$fw\System.Drawing.dll",
  "/r:$fw\System.Management.dll",
  "/r:$fw\System.dll",
  "/r:$fw\System.Core.dll"
) + $sources

Write-Host "Compiling DeskMonitor..."
& $csc @args
if ($LASTEXITCODE -ne 0) { throw "compile failed: $LASTEXITCODE" }
Write-Host "OK  $outDir\DeskMonitor.exe"
Get-Item "$outDir\DeskMonitor.exe" | Format-List FullName, Length, LastWriteTime
Write-Host "=== dist dlls ==="
Get-ChildItem $outDir -Filter "*.dll" | Select-Object Name, Length