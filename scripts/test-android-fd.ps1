param(
    [string]$AndroidSdk = 'C:\Program Files (x86)\Android\android-sdk',
    [string]$JavaHome = 'C:\Program Files\Microsoft\jdk-17.0.20.8-hotspot',
    [string]$Serial = '65XKZD55EMTSKR9D'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$id = [Guid]::NewGuid().ToString('N')
$work = Join-Path $root "artifacts/fd-test/$id"
$classes = Join-Path $work 'classes'
$dex = Join-Path $work 'dex'
New-Item -ItemType Directory -Force -Path $classes,$dex | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'src/SaeParTunnel.AndroidBinding/ReferenceSource/SaeParTunDescriptor.java.txt') -Destination (Join-Path $work 'SaeParTunDescriptor.java')
Copy-Item -LiteralPath (Join-Path $root 'tests/android/SaeParTunDescriptorTest.java.txt') -Destination (Join-Path $work 'SaeParTunDescriptorTest.java')
& (Join-Path $JavaHome 'bin/javac.exe') --release 8 -cp (Join-Path $AndroidSdk 'platforms/android-35/android.jar') -d $classes (Join-Path $work 'SaeParTunDescriptor.java') (Join-Path $work 'SaeParTunDescriptorTest.java')
if ($LASTEXITCODE -ne 0) { throw 'FD fixture compilation failed' }
& (Join-Path $JavaHome 'bin/jar.exe') cf (Join-Path $work 'fixture.jar') -C $classes .
if ($LASTEXITCODE -ne 0) { throw 'Fixture archive failed' }
$env:JAVA_HOME = $JavaHome
& (Join-Path $AndroidSdk 'build-tools/35.0.0/d8.bat') --min-api 26 --lib (Join-Path $AndroidSdk 'platforms/android-35/android.jar') --output $dex (Join-Path $work 'fixture.jar')
if ($LASTEXITCODE -ne 0) { throw 'FD fixture DEX failed' }
& (Join-Path $JavaHome 'bin/jar.exe') cf (Join-Path $work 'fixture-dex.jar') -C $dex .
if ($LASTEXITCODE -ne 0) { throw 'DEX archive failed' }
$adb = Join-Path $AndroidSdk 'platform-tools/adb.exe'
$remote = "/data/local/tmp/saepar-fd-$id.jar"
& $adb -s $Serial push (Join-Path $work 'fixture-dex.jar') $remote
if ($LASTEXITCODE -ne 0) { throw 'FD fixture upload failed' }
try {
    & $adb -s $Serial shell "CLASSPATH=$remote app_process /system/bin com.saepar.tunnel.bridge.SaeParTunDescriptorTest /data/local/tmp/saepar-fd-$id.sentinel"
    if ($LASTEXITCODE -ne 0) { throw 'Android fd regression failed' }
} finally { & $adb -s $Serial shell rm $remote }
