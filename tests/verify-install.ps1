param([string]$Setup = (Join-Path $PSScriptRoot '../artifacts/ClipTap-test-setup.exe'))
$ErrorActionPreference = 'Stop'
$testRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../artifacts/test-results'))
$sandbox = Join-Path $testRoot ('setup-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $sandbox 'app'
$registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ClipTap.InstallerTest_is1'
if (Test-Path -LiteralPath $registration) { throw 'A previous test installation is still registered; inspect it before running again.' }
[IO.Directory]::CreateDirectory($sandbox) | Out-Null
function Run-Installer([string]$File, [string[]]$Arguments) {
    $process = Start-Process -FilePath ([IO.Path]::GetFullPath($File)) -ArgumentList $Arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "Installer failed with exit code $($process.ExitCode). Logs: $sandbox" }
}
try {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=`"$destination`"", "/LOG=`"$sandbox/install.log`"")
    Run-Installer $Setup $arguments
    foreach ($file in @('ClipTap.exe', 'ClipTap.dll', 'coreclr.dll', 'unins000.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $destination $file))) { throw "Installed file missing: $file" }
    }
    if ((Get-ItemProperty -LiteralPath $registration).InstallLocation.TrimEnd('\') -ne $destination) { throw 'Incorrect uninstall registration.' }
    [IO.File]::WriteAllText((Join-Path $destination 'user-owned.txt'), 'preserve-on-upgrade-and-uninstall')
    Run-Installer $Setup $arguments
    if ([IO.File]::ReadAllText((Join-Path $destination 'user-owned.txt')) -ne 'preserve-on-upgrade-and-uninstall') { throw 'Upgrade changed user-owned file.' }
    Run-Installer (Join-Path $destination 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$sandbox/uninstall.log`"")
    if (Test-Path -LiteralPath (Join-Path $destination 'ClipTap.exe')) { throw 'Application was not uninstalled.' }
    if (Test-Path -LiteralPath $registration) { throw 'Uninstall registration was not removed.' }
    if (!(Test-Path -LiteralPath (Join-Path $destination 'user-owned.txt'))) { throw 'Uninstall removed a user-owned file.' }
    Write-Output "PASS real EXE install, upgrade, uninstall registration, bundled runtime and preservation of user-owned files. Logs: $sandbox"
} finally {
    # Keep logs and failed installations for diagnosis. The test AppId never matches production.
    if ((Test-Path -LiteralPath $registration) -and (Test-Path -LiteralPath (Join-Path $destination 'unins000.exe'))) {
        Run-Installer (Join-Path $destination 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART')
    }
}
