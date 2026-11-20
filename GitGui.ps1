[CmdletBinding()]
param(
    [switch]$Build,
    [string]$Repository
)
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'Workspace/bin/Release/net10.0-windows/GitWorkspace.exe'
$stamp = Join-Path (Split-Path -Parent $exe) '.build-stamp'
$sourceChanged = $false
if (Test-Path -LiteralPath $stamp) {
    $builtAt = (Get-Item -LiteralPath $stamp).LastWriteTimeUtc
    $sources = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'Workspace') -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules|vendor)[\\/]' -and $_.Extension -in @('.cs', '.xaml', '.csproj', '.html', '.js', '.css', '.json') }
    $sourceChanged = @($sources | Where-Object { $_.LastWriteTimeUtc -gt $builtAt }).Count -gt 0
}
if ($Build -or $sourceChanged -or -not (Test-Path -LiteralPath $stamp) -or -not (Test-Path -LiteralPath $exe)) {
    & (Join-Path $PSScriptRoot 'Build-Workspace.ps1')
}
$argumentList = @()
if ($PSBoundParameters.ContainsKey('Repository') -and -not [string]::IsNullOrWhiteSpace($Repository)) {
    $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Repository)
    # Start-Process joins ArgumentList with spaces and does not quote; wrap paths that need it.
    $argumentList = @('--repository', ('"{0}"' -f $resolved))
}
if ($argumentList.Count -gt 0) {
    Start-Process -FilePath $exe -ArgumentList $argumentList
}
else {
    Start-Process -FilePath $exe
}
