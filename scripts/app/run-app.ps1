# Builds and launches the packaged TransFlux app (Reactor/WinUI 3), waits for its window,
# and prints process + window info. Requires Developer Mode and Windows App Runtime.
param(
  [string]$Configuration = 'Debug',
  [int]$WaitSeconds = 20
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $root
try {
  Write-Host 'Building TransFlux.App ...'
  dotnet build src/TransFlux.App/TransFlux.App.csproj -c $Configuration | Out-Host
  if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
  Write-Host 'Launching packaged app via dotnet run ...'
  $job = Start-Job -ScriptBlock {
    param($p)
    Set-Location $p
    dotnet run --project src/TransFlux.App/TransFlux.App.csproj -c $using:Configuration --no-build 2>&1
  } -ArgumentList (Get-Location).Path
  Start-Sleep -Seconds $WaitSeconds
  $procs = Get-Process -Name 'TransFlux' -ErrorAction SilentlyContinue
  if (-not $procs) {
    Write-Host 'No TransFlux process found. dotnet run output:'
    Receive-Job $job | Select-Object -Last 30 | Out-Host
    throw 'App did not stay running.'
  }
  $procs | Select-Object Id,MainWindowTitle,Responding | Format-Table -AutoSize | Out-Host
  Write-Host ("PID=" + ($procs[0].Id))
} finally {
  Pop-Location
}
