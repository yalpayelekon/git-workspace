[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$assets = Join-Path $PSScriptRoot 'Workspace/Assets/terminal'
Push-Location $assets
try {
    npm ci --ignore-scripts --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Terminal dependency restore failed.' }
    New-Item -ItemType Directory -Force -Path 'vendor' | Out-Null
    Copy-Item -LiteralPath 'node_modules/@xterm/xterm/lib/xterm.js' -Destination 'vendor/xterm.js'
    Copy-Item -LiteralPath 'node_modules/@xterm/xterm/css/xterm.css' -Destination 'vendor/xterm.css'
    Copy-Item -LiteralPath 'node_modules/@xterm/addon-fit/lib/addon-fit.js' -Destination 'vendor/addon-fit.js'
    Copy-Item -LiteralPath 'node_modules/@xterm/xterm/LICENSE' -Destination 'vendor/LICENSE-xterm.txt'
    Copy-Item -LiteralPath 'node_modules/@xterm/addon-fit/LICENSE' -Destination 'vendor/LICENSE-addon-fit.txt'
} finally { Pop-Location }
dotnet build (Join-Path $PSScriptRoot 'Workspace/Workspace.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Workspace build failed.' }
Set-Content -LiteralPath (Join-Path $PSScriptRoot 'Workspace/bin/Release/net10.0-windows/.build-stamp') -Value ([DateTime]::UtcNow.ToString('O')) -Encoding ASCII
