$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localSdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$project = Join-Path $projectRoot 'plugin/JellyCache.ReadProbe/JellyCache.ReadProbe.csproj'
& $dotnetExe restore $project --locked-mode --nologo -p:NuGetAudit=false
if ($LASTEXITCODE -ne 0) { throw 'Read test restore failed' }
& $dotnetExe build $project -c Release --no-restore --nologo -m:1 -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Read test build failed' }
& (Join-Path $PSScriptRoot 'check-single-plugin.ps1')
