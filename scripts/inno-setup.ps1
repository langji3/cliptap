function Find-InnoCompiler {
    if ($env:INNO_SETUP_COMPILER) {
        if (!(Test-Path -LiteralPath $env:INNO_SETUP_COMPILER)) { throw 'INNO_SETUP_COMPILER does not exist.' }
        return $env:INNO_SETUP_COMPILER
    }
    $cache = Join-Path $projectRoot 'artifacts/tools/inno-6.7.3'
    $compiler = Join-Path $cache 'ISCC.exe'
    if (Test-Path -LiteralPath $compiler) { return $compiler }
    [IO.Directory]::CreateDirectory($cache) | Out-Null
    $download = Join-Path $cache 'innosetup-6.7.3.exe'
    $client = New-Object Net.WebClient
    try { $client.DownloadFile('https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe', $download) }
    finally { $client.Dispose() }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes($download))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
    if ($digest -ne '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732') { throw 'Inno Setup download checksum mismatch.' }
    $arguments = @('/PORTABLE=1', '/CURRENTUSER', '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/TASKS=', "/DIR=`"$cache`"")
    $process = Start-Process -FilePath $download -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $compiler)) { throw 'Could not prepare Inno Setup compiler.' }
    return $compiler
}
