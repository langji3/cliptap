namespace ClipTap.Core;

// Keeps only a configured trigger prefix, never arbitrary typed text.
public sealed class ExpansionMatcher
{
    private Snippet[] _snippets = [];
    private string _prefix = "";
    private long _lastTime;
    public static bool IsValidTrigger(string? trigger) => trigger is { Length: >= 3 and <= 32 } &&
        trigger[0] == '!' && trigger[^1] != '_' &&
        trigger.AsSpan(1).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_".AsSpan()) < 0;
    public static bool CanExpand(string value) => value.Length is > 0 and <= 2000 &&
        !value.Any(c => char.IsControl(c) || c is '\u2028' or '\u2029');

    public void Configure(IEnumerable<Snippet> snippets)
    {
        var valid = snippets.Where(s => IsValidTrigger(s.Trigger) && CanExpand(s.Value)).ToArray();
        // Fail closed for ambiguous data even if it bypassed SaveSnippet validation.
        _snippets = valid.Where(s => !valid.Any(other => !ReferenceEquals(other, s) &&
            (s.Trigger!.StartsWith(other.Trigger!, StringComparison.Ordinal) ||
             other.Trigger!.StartsWith(s.Trigger!, StringComparison.Ordinal)))).ToArray();
        Reset();
    }

    public void Reset() { _prefix = ""; _lastTime = 0; }

    public Snippet? Feed(char character, long milliseconds)
    {
        if (milliseconds - _lastTime > 5000 || milliseconds < _lastTime) Reset();
        _lastTime = milliseconds;
        _prefix = character == '!' ? "!" : _prefix.Length == 0 ? "" : _prefix + character;
        if (_prefix.Length == 0) return null;
        var candidates = _snippets.Where(s => s.Trigger!.StartsWith(_prefix, StringComparison.Ordinal)).ToArray();
        var match = candidates.FirstOrDefault(s => s.Trigger == _prefix);
        if (match is not null || candidates.Length == 0) Reset();
        return match;
    }
}
