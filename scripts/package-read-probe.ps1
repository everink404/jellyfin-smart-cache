param([string]$Tag = 'v0.4.1-management-test.1')
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
Compress-Archive -LiteralPath $assembly,(Join-Path $build 'JellyCache.ReadProbe.deps.json'),(Join-Path $build 'JellyCache.Core.dll') -DestinationPath $package
$md5 = (Get-FileHash -LiteralPath $package -Algorithm MD5).Hash.ToLowerInvariant()
$sha = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = @([ordered]@{
    guid = 'd9ed3154-82f7-4e4b-9b74-24831fc59d65'; name = 'JellyCache single-plugin read test'
    overview = 'Single-plugin same-season prefetch and local cache routing test.'
    description = 'TEST ONLY. Jellyfin 12.1.0. Disabled by default; upgrades retain configuration. Automatic Ahead 2 with bounded storage. Known official 12.1 services use request, ffmpeg, subtitle and attachment lifetime protection; uncertain operations remain protected until restart. Paused-only administrator cleanup skips active readers and refuses unknown files. Full isolated Jellyfin 12.1 cleanup verified on Windows; NAS 0.4.1 acceptance remains pending. Manual reading mode remains available.'
    owner = 'everink404'; category = 'General'
    versions = @([ordered]@{
        version = $version; targetAbi = '12.1.0.0'; checksum = $md5
        changelog = "$Tag. M2 safe management: paused-only cleanup of unoccupied managed cache; preserve active reads and origin fallback. See docs/M2_DELIVERY.md."
        sourceUrl = "https://everink404.github.io/jellyfin-smart-cache/packages/$Tag/$name"
        timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    })
})
ConvertTo-Json -InputObject $manifest -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'manifest.json') -Encoding utf8
"$sha  $name" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
[pscustomobject]@{Package=$package;Version=$version;MD5=$md5;SHA256=$sha;Manifest='Prepared locally; URL is not published by this script'}
