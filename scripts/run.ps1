. "$PSScriptRoot/dotnet-path.ps1"
Push-Location $projectRoot
try {
    & $dotnet build src/ClipTap/ClipTap.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $app = Join-Path $projectRoot 'src/ClipTap/bin/Release/net10.0-windows10.0.19041.0/ClipTap.exe'
    Start-Process -FilePath $app -WorkingDirectory (Split-Path $app) -WindowStyle Hidden
} finally { Pop-Location }
