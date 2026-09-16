$ErrorActionPreference = 'Stop'

function Find-DotNet {
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    $candidates += 'D:\dotnet\dotnet.exe'
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if ((Test-Path -LiteralPath $candidate) -and (& $candidate --list-sdks | Where-Object { $_ -match '^10\.' })) {
            $env:DOTNET_ROOT = Split-Path $candidate
            return $candidate
        }
    }
    throw '.NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0'
}

$dotnet = Find-DotNet
$projectRoot = Split-Path $PSScriptRoot
