param(
    [string]$Version,
    [string]$Configuration = 'Release',
    [string]$WindowsRuntimeIdentifier = 'win10-x64',
    [bool]$WindowsSelfContained = $true,
    [switch]$SkipAndroid,
    [switch]$SkipWindows,
    [string]$AndroidKeyStore,
    [string]$AndroidSigningKeyAlias = 'saepar',
    [string]$AndroidSigningKeyPass,
    [string]$AndroidSigningStorePass,
    [switch]$GenerateLocalAndroidKeyStore
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\SaeParTunnel.App\SaeParTunnel.App.csproj'
$windowsFramework = 'net9.0-windows10.0.19041.0'
$androidFramework = 'net9.0-android'

function Get-ProjectVersion {
    [xml]$projectXml = Get-Content -LiteralPath $appProject -Raw
    $node = $projectXml.SelectSingleNode('/Project/PropertyGroup/ApplicationDisplayVersion')
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw 'ApplicationDisplayVersion was not found in the app project.'
    }

    $node.InnerText.Trim()
}

function Convert-ToReleaseTag([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = Get-ProjectVersion
    }

    $value = $value.Trim()
    if ($value.StartsWith('v', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $value
    }

    "v$value"
}

function Get-ContentHash([string]$path) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-ReleasePassword {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }

    (($bytes | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Ensure-LocalAndroidKeyStore {
    $signingRoot = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.saepar-tunnel'
    $storePath = Join-Path $signingRoot 'android-release.keystore'
    $passwordPath = Join-Path $signingRoot 'android-release-password.txt'

    New-Item -ItemType Directory -Force -Path $signingRoot | Out-Null

    if (Test-Path -LiteralPath $storePath) {
        if (!(Test-Path -LiteralPath $passwordPath)) {
            throw "Local Android keystore exists, but $passwordPath is missing. Restore the password file or pass signing values explicitly."
        }

        $script:AndroidKeyStore = $storePath
        $password = (Get-Content -LiteralPath $passwordPath -Raw).Trim()
        $script:AndroidSigningKeyPass = $password
        $script:AndroidSigningStorePass = $password
        return
    }

    $password = Get-ReleasePassword
    Set-Content -LiteralPath $passwordPath -Value $password -NoNewline -Encoding ascii

    Write-Host "Creating local Android release keystore at $storePath" -ForegroundColor Yellow
    & keytool -genkeypair -v `
        -keystore $storePath `
        -alias $AndroidSigningKeyAlias `
        -keyalg RSA `
        -keysize 2048 `
        -validity 10000 `
        -storepass $password `
        -keypass $password `
        -dname 'CN=SaePar Tunnel, OU=SaePar, O=SaePar, L=Tehran, S=Tehran, C=IR'

    if ($LASTEXITCODE -ne 0) {
        throw 'keytool failed to create the Android release keystore.'
    }

    $script:AndroidKeyStore = $storePath
    $script:AndroidSigningKeyPass = $password
    $script:AndroidSigningStorePass = $password
}

function Find-NewestFile([string[]]$roots, [string]$filter) {
    $files = foreach ($root in $roots) {
        if (![string]::IsNullOrWhiteSpace($root) -and (Test-Path -LiteralPath $root)) {
            Get-ChildItem -LiteralPath $root -Recurse -File -Filter $filter
        }
    }

    if ($null -eq $files) {
        throw "No $filter file was found under: $($roots -join ', ')"
    }

    $files |
        Sort-Object `
            @{ Expression = { if ($_.Name -match '(?i)signed') { 0 } else { 1 } } }, `
            @{ Expression = { $_.LastWriteTimeUtc }; Descending = $true } |
        Select-Object -First 1
}

function Write-Checksums([string]$releaseRoot) {
    $checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
    $assets = Get-ChildItem -LiteralPath $releaseRoot -File |
        Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
        Sort-Object Name

    $lines = foreach ($asset in $assets) {
        "$(Get-ContentHash $asset.FullName)  $($asset.Name)"
    }

    Set-Content -LiteralPath $checksumPath -Value $lines -Encoding ascii
}

$releaseTag = Convert-ToReleaseTag $Version
$assetVersion = $releaseTag -replace '^[vV]', ''
$releaseRoot = Join-Path $repoRoot "artifacts\release\$releaseTag"
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null

Write-Host "Packaging SaePar Tunnel $releaseTag" -ForegroundColor Cyan
Write-Host "Output: $releaseRoot"

if (!$SkipAndroid) {
    if ($GenerateLocalAndroidKeyStore) {
        Ensure-LocalAndroidKeyStore
    }

    if ([string]::IsNullOrWhiteSpace($AndroidKeyStore) -or
        [string]::IsNullOrWhiteSpace($AndroidSigningKeyAlias) -or
        [string]::IsNullOrWhiteSpace($AndroidSigningKeyPass) -or
        [string]::IsNullOrWhiteSpace($AndroidSigningStorePass)) {
        throw 'Android signing values are required. Pass a keystore and passwords, or use -GenerateLocalAndroidKeyStore for a local-only release key.'
    }

    $AndroidKeyStore = [System.IO.Path]::GetFullPath($AndroidKeyStore)
    if (!(Test-Path -LiteralPath $AndroidKeyStore)) {
        throw "Android keystore was not found: $AndroidKeyStore"
    }

    Write-Host 'Publishing signed Android APK...' -ForegroundColor Cyan
    $androidArgs = @(
        'publish', $appProject,
        '-f', $androidFramework,
        '-c', $Configuration,
        '-p:AndroidPackageFormats=apk',
        '-p:AndroidKeyStore=true',
        "-p:AndroidSigningKeyStore=$AndroidKeyStore",
        "-p:AndroidSigningKeyAlias=$AndroidSigningKeyAlias",
        "-p:AndroidSigningKeyPass=$AndroidSigningKeyPass",
        "-p:AndroidSigningStorePass=$AndroidSigningStorePass"
    )
    & dotnet @androidArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Android publish failed.'
    }

    $androidRoots = @(
        $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "SPTBuild\bin\SaeParTunnel.App\$Configuration\$androidFramework" }),
        (Join-Path (Split-Path -Parent $appProject) "bin\$Configuration\$androidFramework")
    )
    $apk = Find-NewestFile $androidRoots '*.apk'
    $apkOut = Join-Path $releaseRoot "SaeParTunnel-$assetVersion-android.apk"
    Copy-Item -LiteralPath $apk.FullName -Destination $apkOut -Force
    Write-Host "Android APK: $apkOut" -ForegroundColor Green
}

if (!$SkipWindows) {
    Write-Host 'Publishing Windows unpackaged app...' -ForegroundColor Cyan
    $windowsArgs = @(
        'publish', $appProject,
        '-f', $windowsFramework,
        '-c', $Configuration,
        "-p:RuntimeIdentifierOverride=$WindowsRuntimeIdentifier",
        '-p:WindowsPackageType=None',
        "-p:WindowsAppSDKSelfContained=$WindowsSelfContained",
        "-p:SelfContained=$WindowsSelfContained"
    )
    & dotnet @windowsArgs
    if ($LASTEXITCODE -ne 0) {
        throw 'Windows publish failed.'
    }

    $windowsPublishRoots = @(
        $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "SPTBuild\bin\SaeParTunnel.App\$Configuration\$windowsFramework\$WindowsRuntimeIdentifier\publish" }),
        (Join-Path (Split-Path -Parent $appProject) "bin\$Configuration\$windowsFramework\$WindowsRuntimeIdentifier\publish")
    )
    $publishDir = $windowsPublishRoots | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($publishDir)) {
        throw "Windows publish directory was not found under: $($windowsPublishRoots -join ', ')"
    }

    $zipOut = Join-Path $releaseRoot "SaeParTunnel-$assetVersion-windows-x64.zip"
    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipOut -Force
    Write-Host "Windows ZIP: $zipOut" -ForegroundColor Green
}

Write-Checksums $releaseRoot
Write-Host ''
Write-Host 'Release assets:' -ForegroundColor Green
Get-ChildItem -LiteralPath $releaseRoot -File | Sort-Object Name | ForEach-Object {
    Write-Host " - $($_.FullName)"
}
