using System.Windows.Input;

namespace ClipTap;

/// <summary>What a key press means while the list page is showing.</summary>
internal enum ListKeyAction
{
    None,
    /// <summary>Show the clipboard tab.</summary>
    HistoryTab,
    /// <summary>Show the snippets tab.</summary>
    SnippetsTab,
    /// <summary>Focus the search box and select the current query (Ctrl+F).</summary>
    FocusSearch,
    ClearQuery,
    HidePanel,
    SelectPrevious,
    SelectNext,
    PasteSelected,
    EditSnippet,
    NewSnippet
}

/// <summary>
/// Pure key routing for the list page. Kept free of WPF state so the focus-source rules (arrows move the
/// selection while the search box holds focus, Escape unwinds one layer at a time) are directly testable.
/// </summary>
internal static class ListKeyRouting
{
    /// <param name="key">The resolved key; IME-composed keys must never reach here.</param>
    /// <param name="modifiers">Currently held modifiers.</param>
    /// <param name="searchFocused">Whether the search box owns keyboard focus.</param>
    /// <param name="query">The current query, already normalised.</param>
    /// <param name="onSnippets">Whether the snippets tab is showing.</param>
    internal static ListKeyAction Resolve(Key key, ModifierKeys modifiers, bool searchFocused, string query, bool onSnippets)
    {
        var control = modifiers.HasFlag(ModifierKeys.Control);
        // With two tabs, both forward and reverse cycling select the other tab.
        if (control && key == Key.Tab) return onSnippets ? ListKeyAction.HistoryTab : ListKeyAction.SnippetsTab;
        if (control && key == Key.F) return ListKeyAction.FocusSearch;
        if (control && key == Key.N && onSnippets) return ListKeyAction.NewSnippet;
        switch (key)
        {
            // Escape unwinds one layer at a time: a non-empty query first, then the panel itself.
            case Key.Escape: return query.Length > 0 ? ListKeyAction.ClearQuery : ListKeyAction.HidePanel;
            // Arrows never switch tabs while the search box holds focus.
            case Key.Left: return searchFocused ? ListKeyAction.None : ListKeyAction.HistoryTab;
            case Key.Right: return searchFocused ? ListKeyAction.None : ListKeyAction.SnippetsTab;
            case Key.Up: return ListKeyAction.SelectPrevious;
            case Key.Down: return ListKeyAction.SelectNext;
            case Key.Enter: return ListKeyAction.PasteSelected;
            case Key.F2 when onSnippets: return ListKeyAction.EditSnippet;
            default: return ListKeyAction.None;
        }
    }
}
