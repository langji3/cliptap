using System.Net;
using System.Net.Http;
using System.Text.Json;
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
