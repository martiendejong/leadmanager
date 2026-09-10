$ErrorActionPreference = 'Continue'

$backend = 'C:\stores\leadmanager\backend'
$dll     = "$backend\LeadManager.Api.dll"
$dotnet  = 'C:\Program Files\dotnet\dotnet.exe'
$svc     = 'LeadManagerApi'
$port    = 5070

Write-Host "=== LeadManager service setup ==="

# Stop + delete existing
sc.exe stop $svc 2>$null
Start-Sleep 2
sc.exe delete $svc 2>$null
Start-Sleep 1

# Remove conflicting bundled dotnet.exe
Remove-Item "$backend\dotnet.exe" -ErrorAction SilentlyContinue
Write-Host "Cleaned bundled dotnet.exe"

# Write batch file wrapper (cmd /c bat works reliably as service)
$bat = "@echo off`r`nset ASPNETCORE_ENVIRONMENT=Production`r`n`"$dotnet`" `"$dll`" --environment Production`r`n"
Set-Content "$backend\run.bat" $bat -Encoding ASCII
Write-Host "Wrote run.bat"

# Create service using sc.exe with cmd /c run.bat
$binPath = "cmd /c `"$backend\run.bat`""
sc.exe create $svc binPath= $binPath start= auto DisplayName= "LeadManager API"
Write-Host "Service created"
Start-Sleep 1

sc.exe start $svc
Start-Sleep 10
sc.exe query $svc

# Health check
try {
    $res = Invoke-WebRequest -Uri "http://localhost:$port/api/health" -UseBasicParsing -TimeoutSec 10
    Write-Host "Health check: $($res.StatusCode)"
} catch {
    Write-Host "Health check failed: $_"
    # Show last 30 lines of any log
    $logPath = "$backend\logs\*.txt"
    if (Test-Path $logPath) {
        Get-ChildItem $logPath | ForEach-Object { Get-Content $_ -Tail 20 }
    }
}

Write-Host "=== Done ==="
