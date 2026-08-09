$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
Write-Host 'Cleaning SaePar Tunnel build artifacts...' -ForegroundColor Cyan

Get-ChildItem -Path (Join-Path $repoRoot 'src') -Directory -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @('bin','obj') } |
    Sort-Object FullName -Descending |
    ForEach-Object {
        Write-Host "Removing $($_.FullName)"
        Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }

$shortBuildRoot = Join-Path $env:LOCALAPPDATA 'SPTBuild'
if (Test-Path $shortBuildRoot) {
    Write-Host "Removing $shortBuildRoot"
    Remove-Item -LiteralPath $shortBuildRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'Clean complete.' -ForegroundColor Green
