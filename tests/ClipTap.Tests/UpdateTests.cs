using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.IO;
using ClipTap.Services;

internal static class UpdateTests
{
    internal static async Task VerifyAsync()
    {
        using var handler = new Handler();
        using var client = new HttpClient(handler);
        var current = new Version(0, 1, 0, 0);
        var portable = new UpdateService(client, false, current);
        Assert((await portable.CheckAsync()).Status == UpdateStatus.Disabled && handler.Calls == 0);
        var installed = new UpdateService(client, true, current);
        string Release(string tag = "v0.2.0", bool draft = false, bool prerelease = false, bool asset = true) =>
            JsonSerializer.Serialize(new { tag_name = tag, draft, prerelease, html_url = "https://untrusted.invalid/", assets = asset ? new[] { new { name = UpdateService.AssetName } } : [] });
        handler.Json = Release();
        var newer = await installed.CheckAsync();
        Assert(newer.Status == UpdateStatus.Available && newer.Version == "0.2.0" &&
            newer.ReleaseUrl?.AbsoluteUri == "https://github.com/langji3/cliptap/releases/tag/v0.2.0");
        foreach (var tag in new[] { "v0.1.0", "v0.0.9", "0.1.0.0" })
        { handler.Json = Release(tag); Assert((await installed.CheckAsync()).Status == UpdateStatus.Current); }
        handler.Json = Release("v0.10.0"); Assert((await installed.CheckAsync()).Status == UpdateStatus.Available);
        foreach (var json in new[] { Release(draft: true), Release(prerelease: true), Release(asset: false), Release("v0.2.0-beta.1"), Release("../../bad"), "[]", "{}", "not-json" })
        { handler.Json = json; Assert((await installed.CheckAsync()).Status == UpdateStatus.Unavailable); }
        handler.Status = HttpStatusCode.NotFound; Assert((await installed.CheckAsync()).Status == UpdateStatus.NoRelease);
        handler.Status = HttpStatusCode.Forbidden; Assert((await installed.CheckAsync()).Status == UpdateStatus.Unavailable);
        handler.Fail = true; Assert((await installed.CheckAsync()).Status == UpdateStatus.Unavailable);
        handler.Fail = false;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert((await installed.CheckAsync(cancelled.Token)).Status == UpdateStatus.Unavailable);
        await VerifyDownloadAsync();
    }

    private static async Task VerifyDownloadAsync()
    {
        byte[] payload = Encoding.UTF8.GetBytes("synthetic installer bytes, never executed");
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        var json = JsonSerializer.Serialize(new { tag_name = "v0.1.2", draft = false, prerelease = false,
            assets = new[] { new { name = UpdateService.AssetName, digest = "sha256:" + hash, size = payload.Length,
                browser_download_url = "https://untrusted.invalid/evil.exe" } } });
        var update = UpdateService.Parse(json, new Version(0, 1, 1));
        Assert(update.DownloadUrl?.AbsoluteUri == "https://github.com/langji3/cliptap/releases/download/v0.1.2/ClipTap-win-x64-setup.exe");
        Assert(update.Sha256 == hash && update.Size == payload.Length);
        using var client = new HttpClient(new DownloadHandler(payload));
        var path = Path.Combine(Path.GetTempPath(), "cliptap-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await UpdateInstaller.DownloadToAsync(client, update, path, new Progress<int>(), CancellationToken.None);
            Assert(File.ReadAllBytes(path).SequenceEqual(payload));
            File.Delete(path);
            foreach (var invalid in new[] { update with { Sha256 = new string('0', 64) }, update with { Size = payload.Length + 1 },
                update with { Size = UpdateInstaller.MaximumSize + 1 }, update with { DownloadUrl = new Uri("https://untrusted.invalid/setup.exe") } })
            {
                var rejected = false;
                try { await UpdateInstaller.DownloadToAsync(client, invalid, path, new Progress<int>(), CancellationToken.None); }
                catch (InvalidDataException) { rejected = true; }
                Assert(rejected);
                File.Delete(path);
            }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var cancelled = false;
            try { await UpdateInstaller.DownloadToAsync(client, update, path, new Progress<int>(), cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled);
        }
        finally { File.Delete(path); }
        var script = UpdateInstaller.BuildScript(@"C:\Temp\it's update\setup.exe", hash,
            @"D:\User's apps\ClipTap\ClipTap.exe", 1234, @"C:\Temp\ready");
        Assert(script.Contains("$target = 'D:\\User''s apps\\ClipTap'") && script.Contains("/VERYSILENT") &&
            script.Contains("/NORESTART") && script.Contains("$parent.WaitForExit(60000)") && script.Contains(".commit"));
        // Parse generated PowerShell only; never execute it or any installer in tests.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var parser = "$tokens=$null; $errors=$null; [System.Management.Automation.Language.Parser]::ParseInput([Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + encoded + "')), [ref]$tokens, [ref]$errors) | Out-Null; if ($errors.Count) { exit 1 }";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(parser)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert(process.ExitCode == 0);
    }

    private sealed class DownloadHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private static void Assert(bool result) { if (!result) throw new InvalidOperationException("Update check regression"); }
    private sealed class Handler : HttpMessageHandler
    {
        internal string Json = "{}";
        internal HttpStatusCode Status = HttpStatusCode.OK;
        internal bool Fail;
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            Assert(request.RequestUri?.AbsoluteUri == "https://api.github.com/repos/langji3/cliptap/releases/latest");
            Assert(request.Headers.UserAgent.Count > 0);
            if (Fail) throw new HttpRequestException("Synthetic offline");
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Json) });
        }
    }
}
