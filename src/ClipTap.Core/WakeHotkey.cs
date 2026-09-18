namespace ClipTap.Core;

public sealed record WakeHotkey(uint Modifiers, uint Key)
{
    public static WakeHotkey Default { get; } = new(1, 0x20);
    public bool IsValid => (Modifiers & ~7u) == 0 && (Modifiers & 3) != 0 &&
        (Key == 0x20 || Key is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7A);
    public override string ToString()
    {
        var parts = new List<string>();
        if ((Modifiers & 2) != 0) parts.Add("Ctrl");
        if ((Modifiers & 1) != 0) parts.Add("Alt");
        if ((Modifiers & 4) != 0) parts.Add("Shift");
        parts.Add(Key == 0x20 ? "空格" : Key >= 0x70 ? $"F{Key - 0x6F}" : ((char)Key).ToString());
        return string.Join("+", parts);
    }
}
