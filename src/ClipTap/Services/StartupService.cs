using Microsoft.Win32;

namespace ClipTap.Services;

internal static class StartupService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool IsEnabled()
    {
        return GetCommand() is not null;
    }
    internal static string? GetCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue("ClipTap") as string;
    }
    internal static void RestoreCommand(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (command is null) key.DeleteValue("ClipTap", throwOnMissingValue: false);
        else key.SetValue("ClipTap", command);
    }
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法找到程序路径。");
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请从发布目录的 ClipTap.exe 设置开机启动。");
            key.SetValue("ClipTap", $"\"{executable}\" --background");
        }
        else key.DeleteValue("ClipTap", throwOnMissingValue: false);
    }
}
