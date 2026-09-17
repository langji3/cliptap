namespace ClipTap.Core;

public sealed record ClipEntry(Guid Id, string Text, DateTimeOffset CopiedAt);

public sealed record Snippet(Guid Id, string Title, string Value, bool IsSensitive, bool IsPinned,
    DateTimeOffset UpdatedAt, string? Trigger = null);

public sealed class AppSettings
{
    public AppearanceMode Theme { get; set; } = AppearanceMode.Light;
    public AccentPalette Accent { get; set; } = AccentPalette.Blue;
    public int HistoryLimit { get; set; } = 100;
    public bool CapturePaused { get; set; }
}

public enum AppearanceMode { System, Light, Dark }
public enum AccentPalette { Green, Blue, Purple }

public sealed class AppState
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public List<ClipEntry> History { get; set; } = [];
    public List<Snippet> Snippets { get; set; } = [];
}
