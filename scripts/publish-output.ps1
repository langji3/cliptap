# Publish into a new directory so removed runtime resources cannot survive from an older build.
function New-PublishStage {
    $artifactRoot = Join-Path $projectRoot 'artifacts'
    New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
    $stage = Join-Path $artifactRoot ('.publish-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    return $stage
}

function Remove-PublishStage([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    $artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
    if ([IO.Path]::GetDirectoryName($resolved) -ne $artifactRoot -or
        [IO.Path]::GetFileName($resolved) -notmatch '^\.publish-[a-f0-9]{32}$') {
        throw "Refusing to remove a path outside the generated publish staging area: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        if ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Publish staging directory must not be a link.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Install-PublishOutput([string]$Stage, [string]$Output) {
    $backup = $null
    if (Test-Path -LiteralPath $Output) {
        # A running portable version belongs to the user; never replace its directory.
        $executable = Join-Path $Output 'ClipTap.exe'
        if (Get-Process ClipTap -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable }) {
            throw "ClipTap is running from $Output. Close that version before publishing to this directory."
        }
        $backup = New-PublishStage
        # Move the previous output inside the generated backup, retaining a rollback copy until promotion succeeds.
        Move-Item -LiteralPath $Output -Destination (Join-Path $backup 'previous')
    }
    $promoted = $false
    try { Move-Item -LiteralPath $Stage -Destination $Output; $promoted = $true }
    catch {
        if ($backup) { Move-Item -LiteralPath (Join-Path $backup 'previous') -Destination $Output }
        throw
    }
    finally {
        # Keep the backup if rollback itself fails.
        if ($backup -and ($promoted -or !(Test-Path -LiteralPath (Join-Path $backup 'previous')))) {
            Remove-PublishStage $backup
        }
    }
}
