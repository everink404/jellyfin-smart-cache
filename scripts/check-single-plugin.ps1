param([switch]$FetchSource)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$commit = 'ee91c75e777da41a9c4f4855e70adc604fbf2ef8'
$researchRoot = Join-Path $projectRoot '.tools/single-plugin-probe'
$sourceRoot = Join-Path $researchRoot "source/jellyfin-$commit"
$expected = @{
    'MediaSourceManager.cs' = '1E69414E32DB4871F0866218959A50DBA8E23D19E171AB71666F28AC6DE21951'
    'LiveStreamHelper.cs' = '8710E64B5A1C3C96DBDE564D1B3333C9A12645E7BCA2011EE6B8AB689AE9E62C'
    'MediaStreamSelector.cs' = '49BF02EDE066E3979A446E602C7DFE407730F6D777536B60ECE5A97328912BAE'
}
if (-not (Test-Path -LiteralPath $sourceRoot)) {
    if (-not $FetchSource) { throw 'Run with -FetchSource to obtain the pinned official Jellyfin source.' }
    New-Item -ItemType Directory -Force $researchRoot | Out-Null
    $archive = Join-Path $researchRoot 'server-source.zip'
    Invoke-WebRequest -Uri "https://codeload.github.com/jellyfin/jellyfin/zip/$commit" -OutFile $archive
    Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $researchRoot 'source') -Force
}
foreach ($name in $expected.Keys) {
    $path = Join-Path $sourceRoot "Emby.Server.Implementations/Library/$name"
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected[$name]) {
        throw "Upstream source digest mismatch: $name"
    }
}
$localSdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$checks = Join-Path $projectRoot 'plugin/SinglePluginChecks/SinglePluginChecks.csproj'
Push-Location $projectRoot
try {
    & $dotnetExe restore $checks --locked-mode --nologo -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Research dependency restore failed' }
    & $dotnetExe build $checks -c Release --no-restore --nologo -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Single-plugin checks build failed' }
    & $dotnetExe run --project $checks -c Release --no-restore --no-build --nologo |
        Tee-Object -FilePath (Join-Path $researchRoot 'checks.log')
    if ($LASTEXITCODE -ne 0) { throw 'Single-plugin source/interface experiment failed' }
} finally { Pop-Location }
