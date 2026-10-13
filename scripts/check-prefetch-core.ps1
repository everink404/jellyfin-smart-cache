$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localSdk = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
$dotnetExe = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$checks = Join-Path $projectRoot 'plugin/CoreChecks/CoreChecks.csproj'
Push-Location $projectRoot
try {
    & $dotnetExe restore $checks --locked-mode --nologo -p:NuGetAudit=false
    if ($LASTEXITCODE -ne 0) { throw 'Core restore failed.' }
    & $dotnetExe run --project $checks -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Core checks failed.' }
} finally { Pop-Location }
