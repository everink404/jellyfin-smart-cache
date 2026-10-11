param([string]$Tag = 'v0.2.0-read-test.2')
$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid tag' }
$projectRoot = Split-Path -Parent $PSScriptRoot
$build = Join-Path $projectRoot 'plugin/JellyCache.ReadProbe/bin/Release/net10.0'
$assembly = Join-Path $build 'JellyCache.ReadProbe.dll'
$version = [System.Reflection.AssemblyName]::GetAssemblyName($assembly).Version.ToString()
$name = "JellyCache.ReadProbe_$version.zip"
$output = Join-Path $projectRoot ".tools/distribution/$Tag"
New-Item -ItemType Directory -Force $output | Out-Null
$package = Join-Path $output $name
if (Test-Path -LiteralPath $package) { throw 'Package already exists; preserve it and use a new tag' }
Compress-Archive -LiteralPath $assembly,(Join-Path $build 'JellyCache.ReadProbe.deps.json') -DestinationPath $package
$md5 = (Get-FileHash -LiteralPath $package -Algorithm MD5).Hash.ToLowerInvariant()
$sha = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = @([ordered]@{
    guid = 'd9ed3154-82f7-4e4b-9b74-24831fc59d65'; name = 'JellyCache single-plugin read test'
    overview = 'Single-episode local cache routing test; no Worker or FUSE.'
    description = 'TEST ONLY. Jellyfin 12.1.0. Disabled by default. Configure one original episode and an identical local copy in the plugin dashboard. No automatic prefetch or eviction. Real NAS playback remains unverified.'
    owner = 'everink404'; category = 'General'
    versions = @([ordered]@{
        version = $version; targetAbi = '12.1.0.0'; checksum = $md5
        changelog = "$Tag. Single-plugin read routing experiment. See docs/READ_PROBE_TEST.md."
        sourceUrl = "https://everink404.github.io/jellyfin-smart-cache/packages/$Tag/$name"
        timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    })
})
ConvertTo-Json -InputObject $manifest -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8
"$sha  $name" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
[pscustomobject]@{Package=$package;Version=$version;MD5=$md5;SHA256=$sha;Manifest='Prepared locally; URL is not published by this script'}
