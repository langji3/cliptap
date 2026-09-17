namespace ClipTap.Core;

// Keeps only a configured trigger prefix and a count of mistyped ASCII characters.
public sealed class ExpansionMatcher
{
    private Snippet[] _snippets = [];
    private string _prefix = "";
    private int _unmatched;
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

    public void Reset() { _prefix = ""; _unmatched = 0; _lastTime = 0; }

    private void AdvanceTime(long milliseconds)
    {
        if (milliseconds - _lastTime > 5000 || milliseconds < _lastTime) Reset();
        _lastTime = milliseconds;
    }

    public void Backspace(long milliseconds)
    {
        AdvanceTime(milliseconds);
        if (_unmatched > 0) _unmatched--;
        else if (_prefix.Length > 0) _prefix = _prefix[..^1];
    }

    public Snippet? Feed(char character, long milliseconds)
    {
        AdvanceTime(milliseconds);
        if (character == '!') { _prefix = "!"; _unmatched = 0; return null; }
        if (_prefix.Length == 0) return null;
        // Only ASCII has an unambiguous one-key/one-backspace relationship here.
        // Bound correction tracking without retaining the wrong characters themselves.
        if (character is < '!' or > '~' || _prefix.Length + _unmatched >= 64) { Reset(); return null; }
        if (_unmatched > 0) { _unmatched++; return null; }
        var candidate = _prefix + character;
        var candidates = _snippets.Where(s => s.Trigger!.StartsWith(candidate, StringComparison.Ordinal)).ToArray();
        if (candidates.Length == 0) { _unmatched = 1; return null; }
        _prefix = candidate;
        var match = candidates.FirstOrDefault(s => s.Trigger == _prefix);
        if (match is not null) Reset();
        return match;
    }
}
