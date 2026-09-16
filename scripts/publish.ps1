param([switch]$SelfContained)
. "$PSScriptRoot/dotnet-path.ps1"
Push-Location $projectRoot
try {
    $flavor = if ($SelfContained) { 'portable' } else { 'framework-dependent' }
    $output = Join-Path $projectRoot "artifacts/ClipTap-win-x64-$flavor"
    $publishArgs = @('publish', 'src/ClipTap/ClipTap.csproj', '-c', 'Release', '-r', 'win-x64', '-o', $output, '--nologo', '-p:DebugType=None', '-p:DebugSymbols=false')
    if ($SelfContained) { $publishArgs += '--self-contained' } else { $publishArgs += '--no-self-contained' }
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE'), (Join-Path $projectRoot 'README.md') -Destination $output
    $archive = "$output.zip"
    Compress-Archive -Path "$output/*" -DestinationPath $archive -Force
    Write-Output $archive
} finally { Pop-Location }
