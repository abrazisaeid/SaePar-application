param([string]$Version)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (!$Version) {
    [xml]$project = Get-Content (Join-Path $repoRoot 'src\SaeParTunnel.App\SaeParTunnel.App.csproj') -Raw
    $Version = $project.SelectSingleNode('/Project/PropertyGroup/ApplicationDisplayVersion').InnerText
}
$Version = $Version -replace '^v', ''
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$releaseRoot = Join-Path $repoRoot "artifacts\release\v$Version"
$zip = Join-Path $releaseRoot "SaeParTunnel-$Version-windows-x64.zip"
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
$installerProject = Join-Path $repoRoot 'tools\SaeParTunnel.WindowsInstaller\SaeParTunnel.WindowsInstaller.csproj'
$outputDir = Join-Path $env:LOCALAPPDATA "SPTBuild\installer\v$Version"
& dotnet publish $installerProject -c Release -o $outputDir "-p:Version=$Version" "-p:PackageZipPath=$zip" "-p:PackageSha256=$hash"
if ($LASTEXITCODE -ne 0) { throw 'Windows installer publish failed.' }
$outExe = Join-Path $releaseRoot "SaeParTunnel-$Version-windows-x64.exe"
Copy-Item -LiteralPath (Join-Path $outputDir 'SaeParTunnel.Setup.exe') -Destination $outExe -Force
$verification = Start-Process -FilePath $outExe -ArgumentList '--verify' -WindowStyle Hidden -Wait -PassThru
if ($verification.ExitCode -ne 0) { throw 'Embedded Windows package verification failed.' }
$checksumLines = Get-ChildItem -LiteralPath $releaseRoot -File |
    Where-Object Name -ne 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object {
        "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
    }
Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Value $checksumLines -Encoding ascii
Write-Output "Windows installation EXE: $outExe"
