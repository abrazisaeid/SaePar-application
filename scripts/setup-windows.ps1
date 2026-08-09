$ErrorActionPreference = 'Stop'

Write-Host 'SaePar Tunnel Cross-platform - .NET 9 setup' -ForegroundColor Cyan
Write-Host ''
Write-Host '1) Checking installed .NET SDKs...'
dotnet --list-sdks
Write-Host ''
Write-Host '2) Checking workloads...'
dotnet workload list
Write-Host ''
Write-Host '3) Repairing/installing the MAUI workload for the installed .NET 9 SDK...'
dotnet workload repair
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Workload repair did not complete; trying workload install maui...' -ForegroundColor Yellow
}
dotnet workload install maui
Write-Host ''
Write-Host 'Setup complete.' -ForegroundColor Green
Write-Host 'Open SaeParTunnel.CrossPlatform.sln in Visual Studio 2022 17.12 or newer.'
Write-Host 'If Visual Studio was open, close and reopen it before building.'
