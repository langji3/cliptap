param([switch]$SelfContained)
. "$PSScriptRoot/dotnet-path.ps1"
. "$PSScriptRoot/publish-output.ps1"
Push-Location $projectRoot
$stage = $null
$temporaryArchive = $null
try {
    $flavor = if ($SelfContained) { 'portable' } else { 'framework-dependent' }
    $output = Join-Path $projectRoot "artifacts/ClipTap-win-x64-$flavor"
    $stage = New-PublishStage
    $publishArgs = @('publish', 'src/ClipTap/ClipTap.csproj', '-c', 'Release', '-r', 'win-x64', '-o', $stage, '--nologo', '-p:ClipTapDistribution=Portable', '-p:DebugType=None', '-p:DebugSymbols=false')
    if ($SelfContained) { $publishArgs += '--self-contained' } else { $publishArgs += '--no-self-contained' }
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE'), (Join-Path $projectRoot 'README.md') -Destination $stage
    $archive = "$output.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $temporaryArchive = "$output-$([Guid]::NewGuid().ToString('N')).zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporaryArchive)
    Install-PublishOutput $stage $output
    $stage = $null
    Move-Item -LiteralPath $temporaryArchive -Destination $archive -Force
    $temporaryArchive = $null
    Write-Output $archive
} finally {
    if ($temporaryArchive -and (Test-Path -LiteralPath $temporaryArchive)) {
        Remove-Item -LiteralPath $temporaryArchive -Force
    }
    if ($stage) { Remove-PublishStage $stage }
    Pop-Location
}
