$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $project 'artifacts\publish'

Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $project 'SonarMiniMixer.App\SonarMiniMixer.App.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish (Join-Path $project 'SonarMiniMixer.Cli\SonarMiniMixer.Cli.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $publish
exit $LASTEXITCODE
