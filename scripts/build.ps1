param([switch]$Integration)
. "$PSScriptRoot/dotnet-path.ps1"
Push-Location $projectRoot
try {
    & $dotnet build src/ClipTap/ClipTap.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $testArgs = @('run', '--project', 'tests/ClipTap.Tests', '-c', 'Release')
    if ($Integration) { $testArgs += @('--', '--integration') }
    & $dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
} finally { Pop-Location }
