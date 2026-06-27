# Builds the distributable installer end-to-end:
#   1. dotnet publish App.UI self-contained (win-x64) -> dist\publish
#      (no .NET needed on target machines)
#   2. ensures the WebView2 Evergreen bootstrapper is in installer\redist
#   3. compiles installer\EpicRpf.iss with Inno Setup -> dist\EpicRpf-Setup-<ver>.exe
# Run from anywhere:  powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

# --- optional Authenticode code signing ----------------------------------------------
# Signing is THE fix for the "unknown publisher" SmartScreen warning and most AV false
# positives. To enable it, set ONE of:
#   $env:EPICRPF_SIGN_THUMBPRINT = "<thumbprint of a cert in CurrentUser\My>"
#   $env:EPICRPF_SIGN_PFX = "C:\path\cert.pfx" ; $env:EPICRPF_SIGN_PASS = "<password>"
# Optionally $env:EPICRPF_SIGN_TS for a custom RFC-3161 timestamp URL.
# Without any of these, the build is simply unsigned (works fine; Windows just warns until
# the download earns SmartScreen reputation). An EV certificate removes the warning instantly.
$signThumb = $env:EPICRPF_SIGN_THUMBPRINT
$signPfx   = $env:EPICRPF_SIGN_PFX
$signPass  = $env:EPICRPF_SIGN_PASS
$tsUrl     = if ($env:EPICRPF_SIGN_TS) { $env:EPICRPF_SIGN_TS } else { "http://timestamp.sectigo.com" }
$signOn    = [bool]($signThumb -or $signPfx)

function Find-SignTool {
  $c = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
       Sort-Object FullName -Descending | Select-Object -First 1
  if ($c) { return $c.FullName }
  $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  return $null
}
function Invoke-Sign($path) {
  if (-not $signOn) { return }
  $st = Find-SignTool
  if (-not $st) { Write-Host "   ! signtool.exe not found (install the Windows SDK) - leaving UNSIGNED"; return }
  $a = @("sign", "/fd", "SHA256", "/tr", $tsUrl, "/td", "SHA256")
  if ($signThumb) { $a += @("/sha1", $signThumb) }
  else { $a += @("/f", $signPfx); if ($signPass) { $a += @("/p", $signPass) } }
  $a += $path
  & $st @a
  if ($LASTEXITCODE -ne 0) { throw "signing failed for $path" }
  Write-Host "   signed $(Split-Path $path -Leaf)"
}

Write-Host "== 1/3 publishing (self-contained win-x64) =="
dotnet publish (Join-Path $root "src\App.UI\App.UI.csproj") -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=false -o (Join-Path $root "dist\publish") -v quiet
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
# Sign the app exe BEFORE Inno bundles it, so the installed app is trusted too.
Invoke-Sign (Join-Path $root "dist\publish\EpicRpf.exe")

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
# Sign the finished installer — this is the file users download, so it's the most important
# one to sign for SmartScreen / antivirus trust.
Invoke-Sign $out.FullName
Write-Host ("OK -> $($out.FullName) ($([math]::Round($out.Length/1MB,1)) MB)" + $(if ($signOn) { " [signed]" } else { " [UNSIGNED - set EPICRPF_SIGN_THUMBPRINT to sign]" }))
