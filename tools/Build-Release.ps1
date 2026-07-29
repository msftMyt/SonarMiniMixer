$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $project 'artifacts'
$publish = Join-Path $artifacts 'publish'
$bundle = Join-Path $artifacts 'SonarMiniMixer-win-x64.zip'
$checksum = "$bundle.sha256"

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $bundle, $checksum -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $project 'SonarMiniMixer.App\SonarMiniMixer.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -p:Deterministic=true -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish (Join-Path $project 'SonarMiniMixer.Cli\SonarMiniMixer.Cli.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -p:Deterministic=true -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $bundle -Force
$hash = (Get-FileHash $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path $checksum -Value "$hash  SonarMiniMixer-win-x64.zip" -NoNewline
Write-Host "Release bundle: $bundle"
Write-Host "SHA256: $hash"
