<#
  Orbital — Windows release build.

  Produces a self-contained, single-file win-x64 exe at bin\publish\win-x64\OrbitalOverlay.exe
  (the file installer\Orbital.iss packages), then — if Inno Setup's `iscc` is on PATH —
  compiles the installer to bin\installer\Orbital-Setup-<version>.exe.

  Usage:
    .\publish.ps1            # build the single-file exe
    .\publish.ps1 -Installer # also compile the Inno Setup installer (needs iscc on PATH)

  Version is read from <Version> in OrbitalOverlay.csproj so it never drifts from the installer.
#>
[CmdletBinding()]
param(
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

# --- Read <Version> from the csproj (single source of truth) ---
[xml]$csproj = Get-Content (Join-Path $root 'OrbitalOverlay.csproj')
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Could not read <Version> from OrbitalOverlay.csproj' }
Write-Host "Orbital release $version" -ForegroundColor Cyan

# --- A running instance locks the exe; stop it before publishing ---
Get-Process OrbitalOverlay -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

# --- Clean previous publish output ---
$outDir = Join-Path $root 'bin\publish\win-x64'
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

# --- Publish: self-contained single-file win-x64 ---
Write-Host 'Publishing self-contained single-file (win-x64)...' -ForegroundColor Cyan
dotnet publish OrbitalOverlay.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -o $outDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

$exe = Join-Path $outDir 'OrbitalOverlay.exe'
if (-not (Test-Path $exe)) { throw "Expected $exe was not produced" }
$sizeMB = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Built: $exe  ($sizeMB MB)" -ForegroundColor Green

# --- Optionally compile the Inno Setup installer ---
if ($Installer) {
    $iscc = (Get-Command iscc -ErrorAction SilentlyContinue)
    if (-not $iscc) {
        Write-Warning 'iscc (Inno Setup) not found on PATH. Install Inno Setup 6, then run: iscc .\installer\Orbital.iss'
    }
    else {
        Write-Host 'Compiling installer with Inno Setup...' -ForegroundColor Cyan
        & $iscc.Source "/DMyAppVersion=$version" (Join-Path $root 'installer\Orbital.iss')
        if ($LASTEXITCODE -ne 0) { throw "iscc failed (exit $LASTEXITCODE)" }
        Write-Host "Installer: bin\installer\Orbital-Setup-$version.exe" -ForegroundColor Green
    }
}
