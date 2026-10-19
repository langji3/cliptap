namespace ClipTap;

/// <summary>
/// Which snippet fields a query may look at. A sensitive snippet is narrowed to
/// <see cref="SnippetMetadata"/> so its body can never be searched.
/// </summary>
internal enum SearchField
{
    /// <summary>Snippet body, included in matching.</summary>
    SnippetText,
    /// <summary>Snippet metadata only (title and trigger); the body must never be searched.</summary>
    SnippetMetadata
}

/// <summary>
/// Pure query matching. Extracted from the window so the sensitive-body boundary and the Chinese and
/// case-insensitive rules are directly testable without a live visual tree.
/// </summary>
internal static class ClipboardSearch
{
    /// <summary>The type label used for image history rows; images have no text to match.</summary>
    internal const string ImageLabel = "图片";

    /// <summary>Trims the query; a whitespace-only query is treated as no query at all.</summary>
    internal static string Normalize(string? query) => query?.Trim() ?? "";

    /// <summary>
    /// Field-set membership for a snippet: a sensitive body is replaced by metadata-only matching.
    /// </summary>
    internal static SearchField SnippetField(bool isSensitive) =>
        isSensitive ? SearchField.SnippetMetadata : SearchField.SnippetText;

    /// <summary>
    /// Ordinal case-insensitive containment. Ordinal keeps Chinese and other non-Latin scripts exact
    /// while still ignoring case for ASCII and for scripts that define case mappings.
    /// </summary>
    internal static bool Matches(string? candidate, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0) return true;
        if (string.IsNullOrEmpty(candidate)) return false;
        return candidate.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Matches a query against the fields a snippet is permitted to expose. The body is only consulted
    /// when <paramref name="field"/> allows it, which is what keeps sensitive values out of the results.
    /// History rows do not go through here: they match on the complete stored text directly.
    /// </summary>
    internal static bool Matches(SearchField field, string title, string? trigger, string? body, string normalizedQuery)
    {
        if (normalizedQuery.Length == 0) return true;
        var searchBody = field == SearchField.SnippetText ? body : null;
        return Matches(title, normalizedQuery)
            || Matches(trigger, normalizedQuery)
            || Matches(searchBody, normalizedQuery);
    }
}
