using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace ClipTap.Services;

internal static class UpdateInstaller
{
    internal const long MaximumSize = 256 * 1024 * 1024;
    private static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };

    internal static async Task<string> DownloadAsync(UpdateResult update, IProgress<int> progress, CancellationToken cancellation)
    {
        if (!UpdateService.IsInstalled) throw new InvalidOperationException("便携版不支持安装更新");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClipTap", "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, UpdateService.AssetName);
        try
        {
            await DownloadToAsync(Client, update, path, progress, cancellation);
            return path;
        }
        catch { File.Delete(path); throw; }
    }

    internal static async Task DownloadToAsync(HttpClient client, UpdateResult update, string path,
        IProgress<int> progress, CancellationToken cancellation)
    {
        if (update.Status != UpdateStatus.Available || update.DownloadUrl is null ||
            update.DownloadUrl.Scheme != "https" || update.DownloadUrl.Host != "github.com" ||
            !update.DownloadUrl.AbsolutePath.StartsWith("/langji3/cliptap/releases/download/", StringComparison.Ordinal) ||
            update.Sha256 is not { Length: 64 } hash || !hash.All(Uri.IsHexDigit) ||
            update.Size <= 0 || update.Size > MaximumSize)
            throw new InvalidDataException("更新包信息不完整，请重新检查更新");
        using var request = new HttpRequestMessage(HttpMethod.Get, update.DownloadUrl);
        request.Headers.UserAgent.ParseAdd("ClipTap/" + UpdateService.VersionText);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != update.Size)
            throw new InvalidDataException("更新包大小不符");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long received = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellation)) != 0)
        {
            received += count;
            if (received > update.Size) throw new InvalidDataException("更新包大小不符");
            digest.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
            progress.Report((int)(received * 100 / update.Size));
        }
        if (received != update.Size || !Convert.ToHexString(digest.GetHashAndReset()).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新包校验失败，请重试");
    }

    internal static async Task StartAsync(string installer, string expectedHash)
    {
        if (!UpdateService.IsInstalled) throw new InvalidOperationException("便携版不支持安装更新");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定安装目录");
        var directory = Path.GetDirectoryName(installer)!;
        var ready = Path.Combine(directory, "ready");
        var script = BuildScript(installer, expectedHash, executable, Environment.ProcessId, ready);
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            @"WindowsPowerShell\v1.0\powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        using var helper = Process.Start(start) ?? throw new IOException("无法启动更新程序");
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(ready)) return;
            if (helper.HasExited) throw new IOException("更新程序启动失败，当前版本继续运行");
            await Task.Delay(100);
        }
        // The helper cannot install until this process exits. Stop it if readiness failed.
        if (!helper.HasExited) helper.Kill();
        throw new IOException("更新程序启动超时，当前版本继续运行");
    }

    internal static string BuildScript(string installer, string hash, string executable, int parentId, string ready)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        var target = Path.GetDirectoryName(executable)!;
        return $$"""
            $ErrorActionPreference = 'Stop'
            $setup = {{Quote(installer)}}
            $app = {{Quote(executable)}}
            $target = {{Quote(target)}}
            $ready = {{Quote(ready)}}
            $log = [IO.Path]::Combine([IO.Path]::GetDirectoryName($setup), 'install.log')
            $parent = Get-Process -Id {{parentId}} -ErrorAction Stop
            [IO.File]::WriteAllText($ready, 'ready')
            if (-not $parent.WaitForExit(60000)) { exit 1 }
            if (-not [IO.File]::Exists($ready + '.commit')) { exit 1 }
            try {
                $stream = [IO.File]::OpenRead($setup)
                try {
                    $sha = [Security.Cryptography.SHA256]::Create()
                    try { $actual = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
                    finally { $sha.Dispose() }
                } finally { $stream.Dispose() }
                if ($actual -ne {{Quote(hash)}}) { throw 'Update checksum mismatch' }
                $arguments = '/VERYSILENT /SUPPRESSMSGBOXES /SP- /NORESTART /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /RESTARTEXITCODE=3010 /DIR="' + $target + '" /LOG="' + $log + '"'
                $process = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -PassThru -Wait
                if ($process.ExitCode -ne 0) { throw ('Installer exit code: ' + $process.ExitCode) }
                Start-Process -FilePath $app -ArgumentList '--background' -WindowStyle Hidden
                Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $ready -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath ($ready + '.commit') -Force -ErrorAction SilentlyContinue
            } catch {
                Add-Type -AssemblyName System.Windows.Forms
                [System.Windows.Forms.MessageBox]::Show('更新未完成，请重新打开 ClipTap 或下载安装包重试。日志：' + $log, 'ClipTap 更新') | Out-Null
            }
            """;
    }
}
