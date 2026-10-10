param(
    [string]$Repository = 'everink404/jellyfin-smart-cache',
    [string]$Tag = 'v0.1.0-test.1'
)

$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Tag -notmatch '^[A-Za-z0-9_.-]+$') {
    throw 'Invalid repository or tag'
}
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildDirectory = Join-Path $projectRoot 'plugin/JellyCache.Plugin/bin/Release/net10.0'
$assemblyPath = Join-Path $buildDirectory 'JellyCache.Plugin.dll'
$version = [System.Reflection.AssemblyName]::GetAssemblyName($assemblyPath).Version.ToString()
$packageName = "JellyCache.Plugin_$version.zip"
$outputDirectory = Join-Path $projectRoot ".tools/distribution/$Tag"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$packagePath = Join-Path $outputDirectory $packageName
if (Test-Path -LiteralPath $packagePath) { throw 'Package already exists; use a new tag or retain the existing artifact' }
Compress-Archive -LiteralPath $assemblyPath,(Join-Path $buildDirectory 'JellyCache.Plugin.deps.json') -DestinationPath $packagePath
$md5 = (Get-FileHash -LiteralPath $packagePath -Algorithm MD5).Hash.ToLowerInvariant()
$sha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = @(
    [ordered]@{
        guid = '7dbbb7e6-2274-498c-af54-203587f64f52'
        name = 'JellyCache experimental bridge'
        overview = 'Experimental episode prefetch event bridge; separate Worker required.'
        description = 'TEST ONLY. Built against Jellyfin 12.1.0 / .NET 10. Disabled by default. Requires a separately configured JellyCache Worker and stable media entry. Real Jellyfin/fnOS loading, FUSE, Docker visibility and playback are not verified. Do not change production /video mounts.'
        owner = 'everink404'
        category = 'General'
        versions = @(
            [ordered]@{
                version = $version
                changelog = "$Tag experimental prerelease. No production compatibility claim. See docs/JELLYFIN_PLUGIN.md for enablement and limitations."
                targetAbi = '12.1.0.0'
                sourceUrl = "https://github.com/$Repository/releases/download/$Tag/$packageName"
                checksum = $md5
                timestamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
            }
        )
    }
)
ConvertTo-Json -InputObject $manifest -Depth 8 | Set-Content -LiteralPath (Join-Path $outputDirectory 'manifest.json') -Encoding utf8
"$sha256  $packageName" | Set-Content -LiteralPath (Join-Path $outputDirectory 'SHA256SUMS.txt') -Encoding ascii
[pscustomobject]@{Package=$packagePath;Version=$version;TargetAbi='12.1.0.0';MD5=$md5;SHA256=$sha256}
