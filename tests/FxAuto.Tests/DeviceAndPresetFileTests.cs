using System.Text;
using FxAuto.Core.Devices;
using FxAuto.Devices;
using FxAuto.FxSound;

namespace FxAuto.Tests;

/// <summary>Task 4b: pure parts of the device monitor (property mapping) and the preset file store (.fac rewrite,
/// lookups by line 3, atomic write, .tmp cleanup) against temporary copies of real FxSound files.</summary>
internal static class DeviceAndPresetFileTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "presets", name);

    private static RawEndpoint G435Raw => new("{0.0.0.00000000}.{b1a2c3d4-1111-4a2b-9c3d-0e1f2a3b4c5d}",
        "Audífono de los auriculares con micrófono (G435 Wireless Gaming Headset)",
        "Audífono de los auriculares con micrófono", "G435 Wireless Gaming Headset", 5,
        new Guid("0c4e12ee-1111-2222-3333-444455556666"), "USB", 1);

    private static RawEndpoint FxRaw => new("{0.0.0.00000000}.{f3c4d5e6-3333-4c4d-9e5f-2a3b4c5d6e7f}",
        "FxSound Speakers (FxSound Audio Enhancer)", "FxSound Speakers", "FxSound Audio Enhancer", 1,
        EndpointMapping.NoContainer, "ROOT", 1);

    private static RawEndpoint RealtekRaw => new("{0.0.0.00000000}.{d2b3c4e5-2222-4b3c-8d4e-1f2a3b4c5d6e}",
        "Altavoces (Realtek(R) Audio)", "Altavoces", "Realtek(R) Audio", 1,
        new Guid("7a1b2c3d-0000-0000-0000-000000000001"), "HDAUDIO", 1);

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "fxauto-tests-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Root);
        public string Sub(string name) { var p = Path.Combine(Root, name); Directory.CreateDirectory(p); return p; }
        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // Data folder with Presets\ (user fixture under an unrelated file name) and Factsoft\2.fac (Music).
    private static (TempDir Dir, PresetFileStore Store, string Presets, string Factsoft) NewStore()
    {
        var t = new TempDir();
        var data = t.Sub("FxSound");
        var presets = Path.Combine(data, "Presets");
        Directory.CreateDirectory(presets);
        var factsoft = t.Sub("Factsoft");
        File.Copy(Fixture("user-G435_Accion.fac"), Path.Combine(presets, "renamed-on-disk.fac"));
        File.Copy(Fixture("factsoft-2.fac"), Path.Combine(factsoft, "2.fac"));
        return (t, new PresetFileStore(data, () => factsoft), presets, factsoft);
    }

    private static List<int> DifferingLines(byte[] a, byte[] b)
    {
        var la = Encoding.UTF8.GetString(a).Split('\n');
        var lb = Encoding.UTF8.GetString(b).Split('\n');
        var diff = new List<int>();
        for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
            if (i >= la.Length || i >= lb.Length || la[i] != lb[i]) diff.Add(i + 1);
        return diff;
    }

    public static void Register()
    {
        // ---- endpoint property mapping ----
        Program.Add("devices: form factor maps 1-9 and falls back to Unknown", () =>
        {
            Assert.Equal(FormFactor.Speakers, EndpointMapping.MapFormFactor(1), "1");
            Assert.Equal(FormFactor.Headphones, EndpointMapping.MapFormFactor(3), "3");
            Assert.Equal(FormFactor.Headset, EndpointMapping.MapFormFactor(5), "5");
            Assert.Equal(FormFactor.Hdmi, EndpointMapping.MapFormFactor(9), "9 (DigitalAudioDisplayDevice)");
            Assert.Equal(FormFactor.Unknown, EndpointMapping.MapFormFactor(0), "0 (RemoteNetworkDevice)");
            Assert.Equal(FormFactor.Unknown, EndpointMapping.MapFormFactor(10), "10 (UnknownFormFactor)");
            Assert.Equal(FormFactor.Unknown, EndpointMapping.MapFormFactor(null), "missing");
        });

        Program.Add("devices: container id normalized, 'no container' sentinel dropped", () =>
        {
            Assert.Null(EndpointMapping.NormalizeContainerId(EndpointMapping.NoContainer), "FxSound sentinel");
            Assert.Null(EndpointMapping.NormalizeContainerId(Guid.Empty), "empty");
            Assert.Null(EndpointMapping.NormalizeContainerId(null), "missing");
            Assert.Equal("{c3482619-0000-0000-0000-0000000000ab}",
                EndpointMapping.NormalizeContainerId(new Guid("C3482619-0000-0000-0000-0000000000AB")), "braces, lower case");
        });

        Program.Add("devices: G435 raw properties map to EndpointInfo", () =>
        {
            var e = EndpointMapping.ToInfo(G435Raw);
            Assert.Equal("Audífono de los auriculares con micrófono (G435 Wireless Gaming Headset)", e.FriendlyName, "friendly");
            Assert.Equal("Audífono de los auriculares con micrófono", e.EndpointDesc, "desc");
            Assert.Equal("G435 Wireless Gaming Headset", e.Adapter, "adapter");
            Assert.Equal(FormFactor.Headset, e.FormFactor, "form factor");
            Assert.Equal("{0c4e12ee-1111-2222-3333-444455556666}", e.ContainerId, "container");
            Assert.Equal("USB", e.Bus, "bus");
            Assert.Equal(1, e.State, "state");
            Assert.True(!e.IsFxSoundVirtual, "not the FxSound endpoint");
        });

        Program.Add("devices: FxSound virtual endpoint detected by bus ROOT + adapter", () =>
        {
            var fx = EndpointMapping.ToInfo(FxRaw);
            Assert.True(fx.IsFxSoundVirtual, "FxSound Speakers");
            Assert.Null(fx.ContainerId, "no container");
            Assert.True(!EndpointMapping.ToInfo(RealtekRaw).IsFxSoundVirtual, "Realtek");
            Assert.True(!EndpointMapping.ToInfo(FxRaw with { Bus = "USB" }).IsFxSoundVirtual, "same adapter on another bus");
        });

        Program.Add("devices: missing friendly name falls back to description, then id; names kept exact", () =>
        {
            var e = EndpointMapping.ToInfo(G435Raw with { FriendlyName = null });
            Assert.Equal("Audífono de los auriculares con micrófono", e.FriendlyName, "desc fallback");
            var e2 = EndpointMapping.ToInfo(G435Raw with { FriendlyName = " ", DeviceDesc = null, Adapter = "" });
            Assert.Equal(G435Raw.EndpointId, e2.FriendlyName, "id fallback");
            Assert.Null(e2.Adapter, "empty adapter → null");
            var e3 = EndpointMapping.ToInfo(G435Raw with { FriendlyName = "Speakers (X) " });
            Assert.Equal("Speakers (X) ", e3.FriendlyName, "not trimmed (must equal output_devices[] exactly)");
        });

        Program.Add("devices: sort puts active endpoints first; SameContent ignores the timestamp", () =>
        {
            var unplugged = EndpointMapping.ToInfo(RealtekRaw with { State = EndpointMapping.StateUnplugged, FriendlyName = "A unplugged" });
            var list = EndpointMapping.Sort([unplugged, EndpointMapping.ToInfo(RealtekRaw), EndpointMapping.ToInfo(G435Raw)]);
            Assert.Equal(1, list[0].State, "active first");
            Assert.Equal(EndpointMapping.StateUnplugged, list[2].State, "unplugged last");
            var a = new EndpointSnapshot(DateTimeOffset.Now, list, FxRaw.EndpointId, FxRaw.EndpointId);
            var b = new EndpointSnapshot(DateTimeOffset.Now.AddSeconds(3), EndpointMapping.Sort(list), FxRaw.EndpointId, FxRaw.EndpointId);
            Assert.True(EndpointMapping.SameContent(a, b), "same content, other time");
            Assert.True(!EndpointMapping.SameContent(a, b with { DefaultMultimediaId = G435Raw.EndpointId }), "default changed");
            Assert.True(!EndpointMapping.SameContent(a, b with { Endpoints = list.Take(2).ToList() }), "endpoint gone");
        });

        // ---- .fac parsing and rewriting ----
        Program.Add("fac: line 3 is the name (user and built-in fixtures)", () =>
        {
            Assert.Equal("G435_Accion", FacFile.ReadName(File.ReadAllBytes(Fixture("user-G435_Accion.fac"))), "user");
            Assert.Equal("Music", FacFile.ReadName(File.ReadAllBytes(Fixture("factsoft-2.fac"))), "built-in");
            Assert.Null(FacFile.ReadName("a\r\nb\r\n"u8), "two lines");
            Assert.Equal("c", FacFile.ReadName("a\nb\nc"u8), "LF, no final newline");
        });

        Program.Add("fac: rewrite changes only line 3 and keeps every other byte", () =>
        {
            var src = File.ReadAllBytes(Fixture("factsoft-2.fac"));
            var dst = FacFile.WithName(src, "Music - G435 copy");
            Assert.Equal("Music - G435 copy", FacFile.ReadName(dst), "new name");
            Assert.Equal(src.Length - "Music".Length + "Music - G435 copy".Length, dst.Length, "length");
            var diff = DifferingLines(src, dst);
            Assert.True(diff.Count == 1 && diff[0] == 3, $"only line 3 differs (got {string.Join(",", diff)})");
            Assert.Equal(src.Count(b => b == '\r'), dst.Count(b => b == '\r'), "CR count (CRLF kept)");
            Assert.True(dst.AsSpan(dst.Length - 2).SequenceEqual("\r\n"u8), "final CRLF kept");
            Assert.True(!(dst[0] == 0xEF && dst[1] == 0xBB), "no BOM added");
        });

        Program.Add("fac: rewrite keeps LF-only files and a BOM if present", () =>
        {
            var lf = FacFile.WithName("x\ny\nOld\nz\n"u8, "New");
            Assert.Equal("x\ny\nNew\nz\n", Encoding.UTF8.GetString(lf), "LF kept");
            byte[] bom = [0xEF, 0xBB, 0xBF, .. "x\r\ny\r\nOld\r\nz"u8];
            var b = FacFile.WithName(bom, "New");
            Assert.True(b.AsSpan(0, 3).SequenceEqual(bom.AsSpan(0, 3)), "BOM kept");
            Assert.Equal("New", FacFile.ReadName(b), "name");
        });

        Program.Add("fac: invalid names and short files are refused", () =>
        {
            Assert.True(FacFile.CheckName("") is not null, "empty");
            Assert.True(FacFile.CheckName("a\nb") is not null, "line break");
            Assert.True(FacFile.CheckName("a/b") is not null, "slash");
            Assert.True(FacFile.CheckName("a:b") is not null, "colon");
            Assert.Null(FacFile.CheckName("G435 - Music_2.v1"), "valid");
            bool threw = false;
            try { FacFile.WithName("only\r\ntwo"u8, "X"); } catch (FormatException) { threw = true; }
            Assert.True(threw, "fewer than 3 lines");
        });

        Program.Add("fac: max_user_presets parsed from FxSound.settings, clamped, default 120", () =>
        {
            static string Xml(string v) => "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<PROPERTIES>\n  <VALUE name=\"language\" val=\"es-ES\"/>\n"
                + $"  <VALUE name=\"max_user_presets\" val=\"{v}\"/>\n</PROPERTIES>\n";
            Assert.Equal(120, FacFile.ParseMaxUserPresets(Xml("120")), "120");
            Assert.Equal(50, FacFile.ParseMaxUserPresets(Xml("50")), "50");
            Assert.Equal(120, FacFile.ParseMaxUserPresets(Xml("500")), "clamped high");
            Assert.Equal(10, FacFile.ParseMaxUserPresets(Xml("3")), "clamped low");
            Assert.Equal(120, FacFile.ParseMaxUserPresets(Xml("abc")), "not a number");
            Assert.Equal(120, FacFile.ParseMaxUserPresets("<PROPERTIES/>"), "missing");
            Assert.Equal(120, FacFile.ParseMaxUserPresets("not xml <"), "garbage");
            Assert.Equal(120, FacFile.ParseMaxUserPresets(null), "no file");
        });

        // ---- PresetFileStore against temporary folders ----
        Program.Add("preset store: finds user presets by line 3, then built-ins in Factsoft", () =>
        {
            var (t, store, presets, factsoft) = NewStore();
            using (t)
            {
                Assert.Equal(Path.Combine(presets, "renamed-on-disk.fac"), store.FindPresetFile("G435_Accion"), "user by line 3");
                Assert.Equal(Path.Combine(factsoft, "2.fac"), store.FindPresetFile("Music"), "built-in");
                Assert.Null(store.FindPresetFile("Nope"), "missing");
                Assert.Null(store.FindPresetFile("music"), "case-sensitive like FxSound names");
                // a user preset named like a built-in wins (Presets\ first)
                File.WriteAllBytes(Path.Combine(presets, "Music.fac"), FacFile.WithName(File.ReadAllBytes(Fixture("factsoft-2.fac")), "Music"));
                Assert.Equal(Path.Combine(presets, "Music.fac"), store.FindPresetFile("Music"), "Presets\\ first");
            }
        });

        Program.Add("preset store: exact copy over the file FxSound created; no .tmp left", () =>
        {
            var (t, store, presets, factsoft) = NewStore();
            using (t)
            {
                // what --save_preset leaves: same name, different formatting ("110: CF" instead of "110.0: CF")
                var cliSaved = Path.Combine(presets, "Spike - Test.fac");
                File.WriteAllText(cliSaved, "CLASS1 : Effect Type\r\n9: Version\r\nSpike - Test\r\n110: CF\r\n");
                var err = store.WriteExactCopy(Path.Combine(factsoft, "2.fac"), "Spike - Test");
                Assert.Null(err, "no error");
                var src = File.ReadAllBytes(Path.Combine(factsoft, "2.fac"));
                Assert.True(File.ReadAllBytes(cliSaved).AsSpan().SequenceEqual(FacFile.WithName(src, "Spike - Test")), "byte-identical except line 3");
                Assert.Equal(0, Directory.GetFiles(presets, "*.tmp").Length, "no .tmp");
                Assert.True(File.ReadAllBytes(Path.Combine(factsoft, "2.fac")).AsSpan().SequenceEqual(src), "source untouched");
            }
        });

        Program.Add("preset store: exact copy creates the file when FxSound has not", () =>
        {
            var (t, store, presets, _) = NewStore();
            using (t)
            {
                var err = store.WriteExactCopy(Path.Combine(presets, "renamed-on-disk.fac"), "G435_Accion copy");
                Assert.Null(err, "no error");
                Assert.Equal(Path.Combine(presets, "G435_Accion copy.fac"), store.FindPresetFile("G435_Accion copy"), "created as <name>.fac");
            }
        });

        Program.Add("preset store: failed write returns the error and deletes .fac.tmp leftovers", () =>
        {
            var (t, store, presets, factsoft) = NewStore();
            using (t)
            {
                var dst = Path.Combine(presets, "Locked.fac");
                File.WriteAllText(dst, "a\r\nb\r\nLocked\r\n");
                File.WriteAllText(Path.Combine(presets, "Old.fac.tmp"), "leftover");
                string? err;
                using (new FileStream(dst, FileMode.Open, FileAccess.Read, FileShare.None))
                    err = store.WriteExactCopy(Path.Combine(factsoft, "2.fac"), "Locked");
                Assert.True(err is not null, "error reported");
                Assert.Equal(0, Directory.GetFiles(presets, "*.fac.tmp").Length, "no .fac.tmp left");
                Assert.Equal("a\r\nb\r\nLocked\r\n", File.ReadAllText(dst), "destination untouched");
            }
        });

        Program.Add("preset store: refuses bad names, missing sources and copying onto the source", () =>
        {
            var (t, store, presets, _) = NewStore();
            using (t)
            {
                Assert.True(store.WriteExactCopy(Path.Combine(presets, "renamed-on-disk.fac"), "a/b") is not null, "bad name");
                Assert.True(store.WriteExactCopy(Path.Combine(presets, "missing.fac"), "X") is not null, "missing source");
                Assert.True(store.WriteExactCopy(Path.Combine(presets, "renamed-on-disk.fac"), "G435_Accion") is not null, "same file");
                Assert.Equal("G435_Accion", FacFile.ReadName(File.ReadAllBytes(Path.Combine(presets, "renamed-on-disk.fac"))), "source intact");
            }
        });

        Program.Add("preset store: AutoSave by file name or line 3; max_user_presets from settings", () =>
        {
            var (t, store, _, _) = NewStore();
            using (t)
            {
                Assert.True(!store.AutoSaveExists("Music"), "no AutoSave folder");
                var auto = Path.Combine(t.Root, "FxSound", "AutoSave");
                Directory.CreateDirectory(auto);
                File.Copy(Fixture("factsoft-2.fac"), Path.Combine(auto, "Music.fac"));
                File.Copy(Fixture("user-G435_Accion.fac"), Path.Combine(auto, "other-name.fac"));
                Assert.True(store.AutoSaveExists("Music"), "by file name");
                Assert.True(store.AutoSaveExists("G435_Accion"), "by line 3");
                Assert.True(!store.AutoSaveExists("General"), "absent");
                Assert.Equal(2, store.AutoSaveNames().Count, "names listed");

                Assert.Equal(120, store.MaxUserPresets(), "no settings file");
                File.WriteAllText(Path.Combine(t.Root, "FxSound", "FxSound.settings"),
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n\n<PROPERTIES>\n  <VALUE name=\"max_user_presets\" val=\"40\"/>\n</PROPERTIES>\n");
                Assert.Equal(40, store.MaxUserPresets(), "from settings");
            }
        });
    }
}
