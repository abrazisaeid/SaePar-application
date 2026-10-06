param(
    [string]$AndroidSdk = 'C:\Program Files (x86)\Android\android-sdk',
    [string]$JavaHome = 'C:\Program Files\Microsoft\jdk-17.0.20.8-hotspot'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path $PSScriptRoot -Parent
$binding = Join-Path $root 'src/SaeParTunnel.AndroidBinding'
$work = Join-Path $root ('artifacts/bridge-build/' + [Guid]::NewGuid().ToString('N'))
$classes = Join-Path $work 'classes'
New-Item -ItemType Directory -Path $classes -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $binding 'ReferenceSource/SaeParXrayBridge.java.txt') -Destination (Join-Path $work 'SaeParXrayBridge.java')
$runtime = [IO.Compression.ZipFile]::OpenRead((Join-Path $binding 'Libraries/libXray.aar'))
try { [IO.Compression.ZipFileExtensions]::ExtractToFile($runtime.GetEntry('classes.jar'), (Join-Path $work 'libXray.jar')) }
finally { $runtime.Dispose() }
$aarPath = Join-Path $binding 'Libraries/SaeParXrayBridge.aar'
$old = [IO.Compression.ZipFile]::OpenRead($aarPath)
try { [IO.Compression.ZipFileExtensions]::ExtractToFile($old.GetEntry('AndroidManifest.xml'), (Join-Path $work 'AndroidManifest.xml')) }
finally { $old.Dispose() }
& (Join-Path $JavaHome 'bin/javac.exe') --release 8 -cp ((Join-Path $AndroidSdk 'platforms/android-35/android.jar') + ';' + (Join-Path $work 'libXray.jar')) -d $classes (Join-Path $work 'SaeParXrayBridge.java')
if ($LASTEXITCODE -ne 0) { throw 'Bridge Java compilation failed.' }
& (Join-Path $JavaHome 'bin/jar.exe') cf (Join-Path $work 'classes.jar') -C $classes .
if ($LASTEXITCODE -ne 0) { throw 'Bridge JAR creation failed.' }
$newAar = Join-Path $work 'SaeParXrayBridge.aar'
$zip = [IO.Compression.ZipFile]::Open($newAar, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in @('AndroidManifest.xml', 'classes.jar')) {
        $entry = $zip.CreateEntry($name)
        $entry.LastWriteTime = [DateTimeOffset]::new(2026, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $source = [IO.File]::OpenRead((Join-Path $work $name))
        $destination = $entry.Open()
        try { $source.CopyTo($destination) }
        finally { $destination.Dispose(); $source.Dispose() }
    }
} finally { $zip.Dispose() }
Copy-Item -LiteralPath $newAar -Destination $aarPath -Force
Get-FileHash -Algorithm SHA256 -LiteralPath $aarPath
