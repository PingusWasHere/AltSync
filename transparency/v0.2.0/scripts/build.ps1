param([switch]$SkipChecks,[switch]$ForRelease)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
if ($ForRelease) {
    $publisherProfile = Get-Content -LiteralPath 'Branding\discord.json' -Raw | ConvertFrom-Json
    [uint64]$publisherIdValue = 0
    if ($publisherProfile.applicationId -notmatch '^[1-9][0-9]{16,19}$' -or -not [uint64]::TryParse([string]$publisherProfile.applicationId,[ref]$publisherIdValue)) { throw 'Set the shared AltSync Application ID in Branding\discord.json before publishing a release.' }
}
& (Join-Path $PSScriptRoot 'brand-assets.ps1')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$dotnetExe = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) {
    $systemDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($systemDotnet -and ((& $systemDotnet.Source --list-sdks) -match '^10\.').Count -gt 0) {
        $dotnetExe = $systemDotnet.Source
    } else {
        Write-Host 'Downloading the official .NET 10 SDK into .tools (no system installation).'
        $metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
        $sdk = $metadata.releases[0].sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
        $archive = Join-Path $projectRoot '.tools\dotnet-sdk.zip'
        New-Item -ItemType Directory -Force (Split-Path $archive -Parent) | Out-Null
        Invoke-WebRequest -UseBasicParsing $sdk.url -OutFile $archive
        if ((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash -ne $sdk.hash) { throw 'Official SDK SHA-512 verification failed.' }
        Expand-Archive -LiteralPath $archive -DestinationPath (Split-Path $dotnetExe -Parent) -Force
    }
}
$env:DOTNET_ROOT = Split-Path $dotnetExe -Parent
if (-not $SkipChecks) {
    & $dotnetExe build tests\Fixture\Fixture.csproj -c Release --nologo
    if ($LASTEXITCODE) { throw 'Fixture build failed.' }
    & $dotnetExe build tests\AltSync.Checks.csproj -c Release --nologo
    if ($LASTEXITCODE) { throw 'Checks build failed.' }
    & $dotnetExe tests\bin\Release\net10.0-windows\AltSync.Checks.dll tests\Fixture\bin\Release\net10.0-windows\RobloxPlayerBeta.exe
    if ($LASTEXITCODE) { throw 'Checks failed.' }
}
& $dotnetExe publish AltSync.csproj -c Release -r win-x64 --self-contained true -p:DebugType=None -p:DebugSymbols=false -o dist\AltSync-App --nologo
if ($LASTEXITCODE) { throw 'Publish failed.' }
Copy-Item -LiteralPath README.md,LISEZ-MOI.txt -Destination dist\AltSync-App
$unexpectedReleaseFiles = @(Get-ChildItem -LiteralPath 'dist\AltSync-App' -File -Recurse | Where-Object {
    $_.Name -match '^(accounts\.json|library\.json|\.env|state\.json|cookies(?:-journal)?|local state|login data)$' -or
    $_.Extension -in @('.cs','.csproj','.sln','.pdb','.user','.ps1','.bat','.cmd','.altsync') -or
    $_.FullName -match '(?i)[\\/](WebView2|node_modules|DiscordBot|Backups|transcripts|Local Storage|Session Storage|IndexedDB)[\\/]'
})
if ($unexpectedReleaseFiles.Count -gt 0) { throw 'Release packaging refused: local account/browser data or development files are present in dist\AltSync-App.' }
Compress-Archive -Path dist\AltSync-App -DestinationPath dist\AltSync-win-x64.zip -Force
Write-Host 'Ready: dist\AltSync-App\AltSync.exe'
