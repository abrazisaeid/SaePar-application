param([switch]$UseCachedInputs)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $repoRoot 'artifacts\iran-routing'
$dataRoot = Join-Path $repoRoot 'src\SaeParTunnel.Core\Data\Iran'
New-Item -ItemType Directory -Force $cache,$dataRoot | Out-Null
$tag = '202609280201'
$sourceHash = '93e5ca20cb4e4db5f71bfa941d319881920d047077c44771d3e4836d9be24cc3'
$source = Join-Path $cache 'qv2ray.json'
$ripePath = Join-Path $cache 'ripe.json'
if (!$UseCachedInputs) {
    Invoke-WebRequest "https://github.com/bootmortis/iran-hosted-domains/releases/download/$tag/qv2ray_schema.json" -UseBasicParsing -TimeoutSec 60 -OutFile $source
    Invoke-WebRequest 'https://stat.ripe.net/data/country-resource-list/data.json?resource=ir&v4_format=prefix' -UseBasicParsing -TimeoutSec 45 -OutFile $ripePath
    Invoke-WebRequest 'https://raw.githubusercontent.com/bootmortis/iran-hosted-domains/main/LICENSE' -UseBasicParsing -TimeoutSec 30 -OutFile (Join-Path $cache 'LICENSE')
}
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceHash) { throw 'Iran domain snapshot checksum mismatch.' }
$domains = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
$ripe = Get-Content -LiteralPath $ripePath -Raw | ConvertFrom-Json
if ($ripe.status -ne 'ok') { throw 'RIPE country resource response is invalid.' }
$normalize = { if ($_ -match '^[a-z]+:') { $_ } else { "domain:$_" } }
$direct = @($domains.domains.direct | ForEach-Object $normalize) + @('domain:xn--mgba3a4f16a')
$direct = @($direct | Sort-Object -Unique)
$proxy = @($domains.domains.proxy | ForEach-Object $normalize | Sort-Object -Unique)
$ips = @(@($ripe.data.resources.ipv4) + @($ripe.data.resources.ipv6) | Sort-Object -Unique)
foreach ($cidr in $ips) {
    $parts = $cidr.Split('/'); $address = $null
    if ($parts.Length -ne 2 -or ![Net.IPAddress]::TryParse($parts[0], [ref]$address) -or [int]$parts[1] -le 0 -or [int]$parts[1] -gt ($address.GetAddressBytes().Length * 8)) { throw "Invalid country prefix: $cidr" }
}
if ($direct.Count -lt 1000 -or $ips.Count -lt 100) { throw 'Iran routing snapshot is incomplete.' }
$snapshot = [ordered]@{DomainSnapshot=$tag;IpSnapshot=$ripe.data.query_time;DirectDomains=$direct;ProxyDomains=$proxy;IpRanges=$ips}
$json = $snapshot | ConvertTo-Json -Depth 5 -Compress
$file = [IO.File]::Create((Join-Path $dataRoot 'catalog.json.gz'))
try {
    $gzip = New-Object IO.Compression.GZipStream($file, [IO.Compression.CompressionMode]::Compress)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes($json); $gzip.Write($bytes,0,$bytes.Length) } finally { $gzip.Dispose() }
} finally { $file.Dispose() }
Copy-Item -LiteralPath (Join-Path $cache 'LICENSE') -Destination (Join-Path $dataRoot 'LICENSE-iran-hosted-domains.txt') -Force
[ordered]@{DomainSource="https://github.com/bootmortis/iran-hosted-domains/releases/tag/$tag";DomainSha256=$sourceHash;IpSource='https://stat.ripe.net/docs/data-api/api-endpoints/country-resource-list';IpSnapshot=$ripe.data.query_time;DirectDomains=$direct.Count;ProxyExceptions=$proxy.Count;IpRanges=$ips.Count} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot 'SOURCES.json') -Encoding utf8
Write-Output "Bundled Iran routes: $($direct.Count) domains, $($proxy.Count) proxy exceptions, $($ips.Count) IPv4/IPv6 prefixes."
