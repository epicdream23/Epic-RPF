# Builds the distributable installer end-to-end:
#   1. dotnet publish App.UI self-contained (win-x64) -> dist\publish
#      (no .NET needed on target machines)
#   2. ensures the WebView2 Evergreen bootstrapper is in installer\redist
#   3. compiles installer\EpicRpf.iss with Inno Setup -> dist\EpicRpf-Setup-<ver>.exe
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

Write-Host "== 1/3 publishing (self-contained win-x64) =="
dotnet publish (Join-Path $root "src\App.UI\App.UI.csproj") -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=false -o (Join-Path $root "dist\publish") -v quiet
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "== 2/3 WebView2 bootstrapper =="
$bs = Join-Path $root "installer\redist\MicrosoftEdgeWebView2Setup.exe"
if (-not (Test-Path $bs)) {
  New-Item -ItemType Directory -Force (Split-Path $bs) | Out-Null
  Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $bs
}
Write-Host "   $bs ($([math]::Round((Get-Item $bs).Length/1KB)) KB)"

Write-Host "== 3/3 compiling installer =="
$iscc = @(
  "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found - install with: winget install JRSoftware.InnoSetup" }

& $iscc (Join-Path $root "installer\EpicRpf.iss") /Q
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$out = Get-ChildItem (Join-Path $root "dist") -Filter "EpicRpf-Setup-*.exe" | Sort-Object LastWriteTime | Select-Object -Last 1
Write-Host "OK -> $($out.FullName) ($([math]::Round($out.Length/1MB,1)) MB)"
