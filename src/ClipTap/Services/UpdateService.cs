using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace ClipTap.Services;

internal enum UpdateStatus { Disabled, Current, Available, NoRelease, Unavailable }
internal sealed record UpdateResult(UpdateStatus Status, string? Version = null, Uri? ReleaseUrl = null);

internal sealed class UpdateService
{
    internal const string AssetName = "ClipTap-win-x64-setup.exe";
    internal static bool IsInstalled => typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .Any(a => a.Key == "ClipTapDistribution" && a.Value == "Installed");
    internal static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 1, 0);
    internal static string VersionText => CurrentVersion.ToString(3);
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 * 1024 };
    private readonly HttpClient _client;
    private readonly bool _installed;
    private readonly Version _current;

    internal UpdateService() : this(Client, IsInstalled, CurrentVersion) { }
    internal UpdateService(HttpClient client, bool installed, Version current)
    { _client = client; _installed = installed; _current = current; }

    internal async Task<UpdateResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!_installed) return new(UpdateStatus.Disabled);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/langji3/cliptap/releases/latest");
            request.Headers.UserAgent.ParseAdd("ClipTap/" + _current.ToString(3));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound) return new(UpdateStatus.NoRelease);
            response.EnsureSuccessStatusCode();
            return Parse(await response.Content.ReadAsStringAsync(cancellationToken), _current);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or FormatException)
        { return new(UpdateStatus.Unavailable); }
    }

    internal static UpdateResult Parse(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False ||
            !root.TryGetProperty("prerelease", out var prerelease) || prerelease.ValueKind != JsonValueKind.False)
            return new(UpdateStatus.Unavailable);
        if (!root.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String ||
            !Version.TryParse(tag.GetString()!.TrimStart('v', 'V'), out var version) || version.Build < 0)
            return new(UpdateStatus.Unavailable);
        var normalized = new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
        var baseline = new Version(current.Major, current.Minor, Math.Max(0, current.Build), Math.Max(0, current.Revision));
        if (normalized <= baseline) return new(UpdateStatus.Current);
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array ||
            !assets.EnumerateArray().Any(a => a.ValueKind == JsonValueKind.Object &&
                a.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == AssetName))
            return new(UpdateStatus.Unavailable);
        // Construct the known repository URL instead of trusting arbitrary links in remote JSON.
        var url = new Uri("https://github.com/langji3/cliptap/releases/tag/" + Uri.EscapeDataString(tag.GetString()!));
        return new(UpdateStatus.Available, version.ToString(), url);
    }
}
