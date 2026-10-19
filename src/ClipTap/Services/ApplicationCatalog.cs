using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipTap.Services;

internal sealed record LaunchableApp(string Id, string Name, string? ExecutablePath = null, ImageSource? Icon = null);

internal interface IApplicationCatalog
{
    Task<IReadOnlyList<LaunchableApp>> ReadAsync();
    bool Launch(LaunchableApp application);
}

/// <summary>Uses the Windows applications folder, including packaged apps. No disk-wide scanning.</summary>
internal sealed class WindowsApplicationCatalog : IApplicationCatalog
{
    private Task<IReadOnlyList<LaunchableApp>>? _read;

    public Task<IReadOnlyList<LaunchableApp>> ReadAsync()
    {
        // A stalled shell extension must not create another COM worker on every summon.
        if (_read is { IsCompleted: false }) return _read;
        var completion = new TaskCompletionSource<IReadOnlyList<LaunchableApp>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _read = completion.Task;
        var worker = new Thread(() =>
        {
            try { completion.SetResult(ReadApplications()); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "ClipTap application catalog" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return _read;
    }

    private static IReadOnlyList<LaunchableApp> ReadApplications()
    {
        object? shell = null, folder = null, items = null;
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Windows application catalog is unavailable.");
            shell = Activator.CreateInstance(type)!;
            folder = ((dynamic)shell).NameSpace("shell:AppsFolder");
            if (folder is null) throw new InvalidOperationException("Windows applications folder is unavailable.");
            items = ((dynamic)folder).Items();
            var result = new List<LaunchableApp>();
            var count = (int)((dynamic)items).Count;
            for (var index = 0; index < count; index++)
            {
                object? item = null;
                try
                {
                    item = ((dynamic)items).Item(index);
                    string name = ((dynamic)item).Name;
                    string id = ((dynamic)item).Path;
                    string? executable = ((dynamic)item).ExtendedProperty("System.Link.TargetParsingPath") as string;
                    // AppsFolder can also contain manuals and web shortcuts. Keep executable and packaged apps.
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                    var desktop = !string.IsNullOrEmpty(executable) && executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                    if (!desktop && !id.Contains('!')) continue;
                    result.Add(new(id, name, desktop ? executable : null, desktop ? ReadIcon(executable!) : null));
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                { /* One stale shell item must not hide the rest of the catalog. */ }
                finally { Release(item); }
            }
            return result.DistinctBy(app => app.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally { Release(items); Release(folder); Release(shell); }
    }

    private static ImageSource? ReadIcon(string executable)
    {
        try
        {
            if (!File.Exists(executable)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(executable);
            if (icon is null) return null;
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or ExternalException)
        { return null; }
    }

    public bool Launch(LaunchableApp application)
    {
        try
        {
            // Shell activation preserves registered app arguments, working directory and packaged-app identity.
            Process.Start(new ProcessStartInfo { FileName = "shell:AppsFolder\\" + application.Id, UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or IOException)
        { return false; }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }
}
