using ClipTap.Branding;
using System.Drawing.Imaging;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
var directory = Path.Combine(root, "src", "ClipTap", "Assets");
Directory.CreateDirectory(directory);
var sizes = new[] { 16, 20, 24, 32, 48, 64, 128, 256 };
var images = sizes.Select(size =>
{
    using var bitmap = IconArtwork.Render(size, Color.FromArgb(40, 120, 93));
    using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
}).ToArray();
using (var output = new BinaryWriter(File.Create(Path.Combine(directory, "cliptap.ico"))))
{
    output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)sizes.Length);
    var offset = 6 + sizes.Length * 16;
    for (var i = 0; i < sizes.Length; i++)
    {
        output.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); output.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
        output.Write((byte)0); output.Write((byte)0); output.Write((ushort)1); output.Write((ushort)32);
        output.Write(images[i].Length); output.Write(offset); offset += images[i].Length;
    }
    foreach (var bytes in images) output.Write(bytes);
}
File.WriteAllBytes(Path.Combine(directory, "cliptap.png"), images[^1]);
Console.WriteLine("Generated 8-size ICO and PNG in " + directory);
