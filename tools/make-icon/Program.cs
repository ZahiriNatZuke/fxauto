using FxAuto.Ui.Settings;

var repo = AppContext.BaseDirectory;
while (repo is not null && !File.Exists(Path.Combine(repo, "FxAuto.sln"))) repo = Path.GetDirectoryName(repo);
var outArg = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
var output = outArg is not null
    ? Path.GetFullPath(outArg)
    : Path.Combine(repo ?? throw new InvalidOperationException("FxAuto.sln not found; pass the output path"), "src", "FxAuto", "app.ico");

var bytes = AppIcon.BuildIco(AppIcon.Verified, AppIcon.IcoSizes);
File.WriteAllBytes(output, bytes);
Console.WriteLine($"{output}: {bytes.Length} bytes, sizes {string.Join(", ", AppIcon.IcoSizes)}");

// Optional (--preview) strip next to the .ico: every size side by side, for a visual check.
if (args.Contains("--preview"))
{
    var sizes = AppIcon.IcoSizes;
    using var strip = new Bitmap(sizes.Sum() + 8 * (sizes.Length + 1), 256 + 16);
    using (var g = Graphics.FromImage(strip))
    {
        g.Clear(Color.White);
        int x = 8;
        foreach (var s in sizes)
        {
            using var b = AppIcon.Render(s, AppIcon.Verified, paused: false);
            g.DrawImageUnscaled(b, x, 8);
            x += s + 8;
        }
    }
    var png = Path.ChangeExtension(output, ".preview.png");
    strip.Save(png, System.Drawing.Imaging.ImageFormat.Png);
    Console.WriteLine(png);
}
