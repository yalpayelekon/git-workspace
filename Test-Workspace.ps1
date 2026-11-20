$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Build-Workspace.ps1')
dotnet run --project (Join-Path $PSScriptRoot 'SmokeTests/SmokeTests.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Workspace smoke checks failed.' }
