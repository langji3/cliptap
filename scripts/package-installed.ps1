param([switch]$TestInstaller)
. "$PSScriptRoot/dotnet-path.ps1"
. "$PSScriptRoot/inno-setup.ps1"
Push-Location $projectRoot
try {
    $output = Join-Path $projectRoot 'artifacts/ClipTap-win-x64-setup'
    & $dotnet publish src/ClipTap/ClipTap.csproj -c Release -r win-x64 -o "$output/app" --nologo --self-contained -p:ClipTapDistribution=Installed -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Installed build failed.' }
    Copy-Item -LiteralPath "$projectRoot/LICENSE" -Destination "$output/app"
    $compiler = Find-InnoCompiler
    [xml]$project = [IO.File]::ReadAllText((Join-Path $projectRoot 'src/ClipTap/ClipTap.csproj'))
    $version = [string]$project.Project.PropertyGroup.Version
    $compileArgs = @('/Qp', "/DAppVersion=$version", "/DPayloadDir=$output/app", "/DOutputDir=$projectRoot/artifacts")
    if ($TestInstaller) { $compileArgs += '/DInstallerTest=1' }
    & $compiler @compileArgs "$projectRoot/installer/ClipTap.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Setup compilation failed.' }
    $fileName = if ($TestInstaller) { 'ClipTap-test-setup.exe' } else { 'ClipTap-win-x64-setup.exe' }
    Write-Output (Join-Path $projectRoot "artifacts/$fileName")
} finally { Pop-Location }
