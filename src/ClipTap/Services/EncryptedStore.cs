using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using ClipTap.Core;

namespace ClipTap.Services;

public sealed class EncryptedStore(string directory)
{
    private static readonly byte[] Header = "CLIPTAP1"u8.ToArray();
    public string FilePath { get; } = Path.Combine(directory, "library.dat");

    public AppState Load()
    {
        if (!File.Exists(FilePath)) return new AppState();
        if (new FileInfo(FilePath).Length > 256 * 1024 * 1024)
            throw new InvalidDataException("数据文件超过大小限制。");
        var encrypted = File.ReadAllBytes(FilePath);
        if (encrypted.Length <= Header.Length || !encrypted.AsSpan(0, Header.Length).SequenceEqual(Header))
            throw new InvalidDataException("数据文件损坏或版本不受支持。原文件已保留。");
        var plain = Transform(encrypted[Header.Length..], protect: false);
        try { return JsonSerializer.Deserialize<AppState>(plain) ?? throw new InvalidDataException("数据为空。"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(AppState state)
    {
        Directory.CreateDirectory(directory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(state);
        byte[] encrypted;
        try { encrypted = Transform(plain, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(Header);
                stream.Write(encrypted);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static byte[] Transform(byte[] value, bool protect)
    {
        var input = new Blob { Size = value.Length, Data = Marshal.AllocHGlobal(value.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(value, 0, input.Data, value.Length);
            var ok = protect
                ? CryptProtectData(ref input, null, nint.Zero, nint.Zero, nint.Zero, 1, out output)
                : CryptUnprotectData(ref input, nint.Zero, nint.Zero, nint.Zero, nint.Zero, 1, out output);
            if (!ok) throw new CryptographicException("无法使用当前 Windows 用户解密或加密数据。", new Win32Exception(Marshal.GetLastWin32Error()));
            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            for (var i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
            Marshal.FreeHGlobal(input.Data);
            if (output.Data != nint.Zero)
            {
                for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Size; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref Blob input, string? description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
