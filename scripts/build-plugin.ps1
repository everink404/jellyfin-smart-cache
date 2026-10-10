$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localSdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$pluginProject = Join-Path $projectRoot 'plugin/JellyCache.Plugin/JellyCache.Plugin.csproj'
$checksProject = Join-Path $projectRoot 'plugin/ContractChecks/ContractChecks.csproj'
& $dotnetExe restore $pluginProject --locked-mode --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin restore failed (requires .NET 10 SDK and NuGet access)' }
& $dotnetExe build $pluginProject -c Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed' }
& $dotnetExe run --project $checksProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Bridge contract checks failed' }
