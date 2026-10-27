using FxAuto.Audio;

namespace FxAuto.Tests;

/// <summary>Task 4a: pure parts of the Windows audio layer (process tree climbing, snapshot building).</summary>
internal static class AudioLayerTests
{
    private const string Fx = "{fxsound-speakers}";
    private const string G435 = "{g435}";
    private const string Realtek = "{realtek}";
    private const int Own = 999;

    private static Dictionary<int, ProcEntry> Map(params (int Pid, int Parent, string Exe)[] rows) =>
        rows.ToDictionary(r => r.Pid, r => new ProcEntry(r.Pid, r.Parent, r.Exe));

    private static Func<int, long?> Starts(params (int Pid, long Start)[] rows)
    {
        var d = rows.ToDictionary(r => r.Pid, r => r.Start);
        return pid => d.TryGetValue(pid, out long s) ? s : null;
    }

    private static readonly Func<int, long?> NoStarts = _ => null;

    private static RawSession Raw(string ep, int pid, string? exe, int state = SessionStates.Active, float peak = 0.5f,
        bool system = false, string? sid = null, int? root = null, string? rootExe = null) =>
        new(ep, pid, exe, state, system, sid, root ?? pid, rootExe ?? exe, null, null, peak);

    public static void Register()
    {
        // ---- ProcessTree climbing ----
        Program.Add("ProcessTree: Chrome audio utility child climbs to the browser process", () =>
        {
            var map = Map((1, 0, "explorer.exe"), (28948, 1, "chrome.exe"), (33668, 28948, "chrome.exe"),
                (40000, 28948, "chrome.exe"));
            var (root, exe) = ProcessTreeLogic.Climb(33668, map, NoStarts);
            Assert.Equal(28948, root, "root pid");
            Assert.Equal("chrome.exe", exe, "root exe");
        });

        Program.Add("ProcessTree: Edge child climbs to msedge.exe, not past a different exe", () =>
        {
            var map = Map((10, 5, "explorer.exe"), (5, 0, "winlogon.exe"), (20, 10, "msedge.exe"), (21, 20, "msedge.exe"));
            Assert.Equal((20, (string?)"msedge.exe"), ProcessTreeLogic.Climb(21, map, NoStarts), "edge root");
        });

        Program.Add("ProcessTree: exe comparison is case-insensitive", () =>
        {
            var map = Map((10, 1, "Chrome.exe"), (11, 10, "chrome.EXE"));
            Assert.Equal(10, ProcessTreeLogic.Climb(11, map, NoStarts).RootPid, "root");
        });

        Program.Add("ProcessTree: WebView2 children climb to the first non-WebView2 host", () =>
        {
            var map = Map((1, 0, "explorer.exe"), (100, 1, "ms-teams.exe"), (200, 100, "msedgewebview2.exe"),
                (201, 200, "msedgewebview2.exe"));
            Assert.Equal((100, (string?)"ms-teams.exe"), ProcessTreeLogic.Climb(201, map, NoStarts), "host");
        });

        Program.Add("ProcessTree: WebView2 host then climbs its own same-exe chain", () =>
        {
            var map = Map((1, 0, "explorer.exe"), (100, 1, "Discord.exe"), (101, 100, "Discord.exe"),
                (200, 101, "msedgewebview2.exe"));
            Assert.Equal(100, ProcessTreeLogic.Climb(200, map, NoStarts).RootPid, "root");
        });

        Program.Add("ProcessTree: orphan WebView2 stays itself", () =>
        {
            var map = Map((200, 77, "msedgewebview2.exe"));
            Assert.Equal((200, (string?)"msedgewebview2.exe"), ProcessTreeLogic.Climb(200, map, NoStarts), "self");
        });

        Program.Add("ProcessTree: a reused parent PID (created after the child) is not followed", () =>
        {
            var map = Map((50, 1, "chrome.exe"), (60, 50, "chrome.exe"));
            var (root, _) = ProcessTreeLogic.Climb(60, map, Starts((60, 1000), (50, 2000)));
            Assert.Equal(60, root, "reused parent ignored");
            Assert.Equal(50, ProcessTreeLogic.Climb(60, map, Starts((60, 2000), (50, 1000))).RootPid, "real parent");
        });

        Program.Add("ProcessTree: unknown pid, cycles and system parents are safe", () =>
        {
            Assert.Equal((42, (string?)null), ProcessTreeLogic.Climb(42, Map(), NoStarts), "unknown");
            var cycle = Map((7, 8, "a.exe"), (8, 7, "a.exe"));
            int r = ProcessTreeLogic.Climb(7, cycle, NoStarts).RootPid;
            Assert.True(r is 7 or 8, "cycle terminates");
            var sys = Map((4, 0, "System"), (300, 4, "System"));
            Assert.Equal(300, ProcessTreeLogic.Climb(300, sys, NoStarts).RootPid, "never climbs into pid 4");
        });

        // ---- snapshot building ----
        Program.Add("Snapshot: FxSound, system sounds and own process excluded; peaks only on FxSound endpoint", () =>
        {
            var raws = new List<RawSession>
            {
                Raw(Fx, 33668, "chrome.exe", peak: 0.69f, root: 28948),
                Raw(Fx, 4240, "FxSound.exe", peak: 0.69f),
                Raw(Fx, 0, null, system: true),
                Raw(Fx, Own, "FxAuto.exe"),
                Raw(G435, 23000, "Discord.exe", peak: 0.4f),
                Raw(Realtek, 4240, "FxSound.exe"),
            };
            var s = AudioSnapshotBuilder.Build(DateTimeOffset.UnixEpoch, Fx, raws, Own);
            Assert.Equal(2, s.Sessions.Count, "chrome + discord");
            var chrome = s.Sessions.Single(x => x.Pid == 33668);
            Assert.Equal(28948, chrome.RootPid, "root mapped");
            Assert.Equal(0.69f, chrome.Peak, "chrome metered");
            var discord = s.Sessions.Single(x => x.Pid == 23000);
            Assert.Equal(G435, discord.EndpointId, "discord endpoint reported");
            Assert.Equal(0f, discord.Peak, "physical endpoint not metered");
            Assert.True(discord.Active, "state kept");
            Assert.Equal(Realtek, s.FxSoundRenderEndpointId, "FxSound renders on Realtek");
            Assert.True(AudioSnapshotBuilder.AnyMeteredActive(s), "active on FxSound");
        });

        Program.Add("Snapshot: FxSound recognised by session identifier path too", () =>
        {
            var r = Raw(G435, 4240, null, sid: @"{0.0.0.00000000}.{x}|\Device\HarddiskVolume3\Program Files\FxSound LLC\FxSound\FxSound.exe%b{00000000}");
            Assert.True(AudioSnapshotBuilder.IsFxSound(r), "by sid");
            Assert.Equal(G435, AudioSnapshotBuilder.FxSoundRenderEndpoint([r], Fx), "render endpoint");
        });

        Program.Add("Snapshot: render endpoint null when FxSound's physical session is inactive", () =>
        {
            var raws = new[] { Raw(Realtek, 4240, "FxSound.exe", state: SessionStates.Inactive), Raw(Fx, 4240, "FxSound.exe") };
            Assert.Null(AudioSnapshotBuilder.FxSoundRenderEndpoint(raws, Fx), "inactive → null");
        });

        Program.Add("Snapshot: inactive and expired sessions; no metering without FxSound endpoint", () =>
        {
            var raws = new List<RawSession>
            {
                Raw(Fx, 10, "spotify.exe", state: SessionStates.Inactive, peak: 0.3f),
                Raw(Fx, 11, "vlc.exe", state: SessionStates.Expired),
            };
            var s = AudioSnapshotBuilder.Build(DateTimeOffset.UnixEpoch, Fx, raws, Own);
            Assert.Equal(1, s.Sessions.Count, "expired dropped");
            Assert.Equal(0f, s.Sessions[0].Peak, "inactive not metered");
            Assert.True(!AudioSnapshotBuilder.AnyMeteredActive(s), "nothing active");
            Assert.True(!AudioSnapshotBuilder.NeedsMetering(Raw(Fx, 12, "a.exe"), null, Own), "no fx endpoint → no meter");
        });

        Program.Add("Snapshot: structure key ignores peaks and changes with state", () =>
        {
            var a = AudioSnapshotBuilder.Build(DateTimeOffset.UnixEpoch, Fx, [Raw(Fx, 10, "a.exe", peak: 0.1f)], Own);
            var b = AudioSnapshotBuilder.Build(DateTimeOffset.UnixEpoch.AddSeconds(1), Fx, [Raw(Fx, 10, "a.exe", peak: 0.9f)], Own);
            var c = AudioSnapshotBuilder.Build(DateTimeOffset.UnixEpoch, Fx,
                [Raw(Fx, 10, "a.exe", state: SessionStates.Inactive)], Own);
            Assert.Equal(AudioSnapshotBuilder.StructureKey(a), AudioSnapshotBuilder.StructureKey(b), "peak-only change");
            Assert.True(AudioSnapshotBuilder.StructureKey(a) != AudioSnapshotBuilder.StructureKey(c), "state change");
        });
    }
}
