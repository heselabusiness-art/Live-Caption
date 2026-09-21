# Rebuilds, registers, and starts the Windows Live Captions host.
# Chrome cannot read LiveCaptions.exe itself — this process is required.

$ErrorActionPreference = "Stop"
$HostId = "com.livecaption.assistant"
$ExtensionId = "nkcgnofkagfllgbfompiejcdadpbbikp"
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $PSScriptRoot "LiveCaptionNativeHost.csproj"
$InstallRoot = Join-Path $env:LOCALAPPDATA "LiveCaptionAssistant"
$HostDir = Join-Path $InstallRoot "host"
$PublishDir = Join-Path $PSScriptRoot "publish"
$ExeName = "LiveCaptionNativeHost.exe"

Write-Host "Publishing Live Caption native host..."
Get-Process -Name "LiveCaptionNativeHost" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish $Project -c Release -r win-x64 --self-contained false -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit $LASTEXITCODE" }

New-Item -ItemType Directory -Force -Path $HostDir | Out-Null
robocopy $PublishDir $HostDir /E /NFL /NDL /NJH /NJS /nc /ns /np | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Failed to copy host files (robocopy exit $LASTEXITCODE)" }

$exe = (Join-Path $HostDir $ExeName)
$manifest = @{
    name = $HostId
    description = "Windows Live Captions bridge for Live Caption Assistant"
    path = $exe
    type = "stdio"
    allowed_origins = @("chrome-extension://$ExtensionId/")
} | ConvertTo-Json

$manifestPath = Join-Path $HostDir "$HostId.json"
Set-Content -Path $manifestPath -Value $manifest -Encoding UTF8

$regPath = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostId"
New-Item -Path $regPath -Force | Out-Null
Set-ItemProperty -Path $regPath -Name "(default)" -Value $manifestPath

# Do not use the CRX registry installer — modern Chrome blocks it.
Remove-Item "HKCU:\Software\Google\Chrome\Extensions\$ExtensionId" -Recurse -ErrorAction SilentlyContinue

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
New-Item -Path $runKey -Force | Out-Null
Set-ItemProperty -Path $runKey -Name "LiveCaptionNativeHost" -Value "`"$exe`" --standalone"

Start-Process -FilePath $exe -ArgumentList "--standalone" -WindowStyle Hidden
Start-Sleep -Seconds 3

try {
    $health = Invoke-RestMethod -Uri "http://127.0.0.1:17891/" -TimeoutSec 3
    Write-Host "Host is running. Status: $($health.status)"
} catch {
    Write-Host "Host started, but the HTTP check did not respond yet. Open Live Captions (Win+Ctrl+L) and click Start in the sidebar."
}

Write-Host ""
Write-Host "Native host installed."
Write-Host "  Extension folder: $Root"
Write-Host "  Host exe:        $exe"
Write-Host "  Expected ID:     $ExtensionId"
Write-Host ""
Write-Host "In Chrome: chrome://extensions -> Developer mode ON -> Load unpacked -> select the folder above."
