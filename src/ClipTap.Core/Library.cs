namespace ClipTap.Core;

public sealed class Library
{
    public const int MaxTextLength = 20_000;
    public const int MaxSnippets = 500;
    public AppState State { get; }

    public Library(AppState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Version != 1 || state.Settings is null || state.History is null || state.Snippets is null)
            throw new InvalidDataException("不支持的数据格式。请保留原文件并检查应用版本。");
        State = state;
        if (!Enum.IsDefined(State.Settings.Theme)) State.Settings.Theme = AppearanceMode.Light;
        if (!Enum.IsDefined(State.Settings.Accent)) State.Settings.Accent = AccentPalette.Green;
        State.Snippets = state.Snippets
            .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Title) && s.Value is not null)
            .DistinctBy(s => s.Id).Take(MaxSnippets).ToList();
        // Legacy history is retained for compatibility only; the UI reads Windows history directly.
        State.History = state.History.Where(c => c is not null && c.Text is not null).ToList();
        RemoveSensitiveHistory();
    }

    public Library WithSettings(AppSettings settings) => new(new AppState
    {
        Version = State.Version,
        Settings = new AppSettings
        {
            HistoryLimit = settings.HistoryLimit,
            CapturePaused = settings.CapturePaused,
            Theme = settings.Theme,
            Accent = settings.Accent
        },
        History = [.. State.History],
        Snippets = [.. State.Snippets]
    });

    public void SaveSnippet(Snippet snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet.Title) || snippet.Title.Trim().Length > 80)
            throw new ArgumentException("标题需要 1–80 个字符。");
        if (string.IsNullOrEmpty(snippet.Value) || snippet.Value.Length > MaxTextLength)
            throw new ArgumentException($"内容需要 1–{MaxTextLength:N0} 个字符。");
        var index = State.Snippets.FindIndex(s => s.Id == snippet.Id);
        if (index < 0 && State.Snippets.Count >= MaxSnippets)
            throw new ArgumentException("最多保存 500 个片段，请先删除不再使用的片段。");
        snippet = snippet with { Title = snippet.Title.Trim() };
        if (index < 0) State.Snippets.Add(snippet);
        else State.Snippets[index] = snippet;
        RemoveSensitiveHistory();
    }

    private void RemoveSensitiveHistory()
    {
        var secrets = State.Snippets.Where(s => s.IsSensitive).Select(s => s.Value).ToHashSet(StringComparer.Ordinal);
        State.History.RemoveAll(c => secrets.Contains(c.Text));
    }

    public IEnumerable<Snippet> OrderedSnippets() => State.Snippets
        .OrderByDescending(s => s.IsPinned).ThenByDescending(s => s.UpdatedAt);

    public static string Preview(string text, int length = 88)
    {
        var flat = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return flat.Length <= length ? flat : flat[..length] + "…";
    }
}
