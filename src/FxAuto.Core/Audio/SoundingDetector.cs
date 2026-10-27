namespace FxAuto.Core.Audio;

/// <summary>REQ-101 hysteresis per root process (plan "Key algorithms" 1).
/// <para>A sample is audible when an active session of the root has <c>peak &gt; PeakThreshold</c>. Only sessions on
/// <see cref="AudioSnapshot.FxSoundEndpointId"/> count (all sessions when it is null, e.g. diagnostics).</para>
/// <para>Entry: within the last <c>AttackMs</c> at least <c>AttackRatio</c> of the samples are audible <b>and</b> the
/// current audible run (gaps up to <c>AttackMs × (1 − AttackRatio)</c> tolerated) lasts ≥ <c>AttackMs</c> for a
/// foreground source or for a source that played during the last <c>ReleaseMs</c>, and ≥ <c>BurstIgnoreMs</c> for any
/// other background source (a notification never enters; the game in front enters in 1.5 s).</para>
/// <para>Exit: no audible sample for <c>ReleaseMs</c>, or <c>GameReleaseMs</c> when <c>isGame</c>.</para>
/// Not thread-safe: coordinator thread only.</summary>
public sealed class SoundingDetector
{
    private readonly SoundingTimings _t;
    private readonly IClock _clock;
    private readonly Dictionary<int, Root> _roots = [];

    private sealed class Root
    {
        public string? Exe;
        public readonly Queue<(DateTimeOffset At, bool Audible)> Window = new();
        public DateTimeOffset? RunStart;
        public DateTimeOffset? LastAudibleAt;
        public DateTimeOffset? LastSampleAt;
        public bool Playing;
        public DateTimeOffset? AudibleSince;
        public DateTimeOffset? ReleasedAt;
        public bool IsGame;
    }

    public SoundingDetector(SoundingTimings t, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(clock);
        _t = t;
        _clock = clock;
    }

    public IReadOnlyList<PlayingState> Observe(AudioSnapshot s, Func<int, bool> isGame, Func<int, bool> isForeground)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(isGame);
        ArgumentNullException.ThrowIfNull(isForeground);

        var at = s.At;
        var now = _clock.Now > at ? _clock.Now : at;
        var attack = TimeSpan.FromMilliseconds(_t.AttackMs);
        var gapTolerance = TimeSpan.FromMilliseconds(Math.Max(_t.PollMs, _t.AttackMs * (1 - _t.AttackRatio)));

        // Audible per root in this snapshot.
        var audibleNow = new Dictionary<int, bool>();
        foreach (var x in s.Sessions)
        {
            if (s.FxSoundEndpointId is not null && !string.Equals(x.EndpointId, s.FxSoundEndpointId, StringComparison.OrdinalIgnoreCase))
                continue;
            var audible = x.Active && x.Peak > _t.PeakThreshold;
            if (!_roots.TryGetValue(x.RootPid, out var r)) _roots[x.RootPid] = r = new Root();
            if (x.RootExe is not null) r.Exe = x.RootExe;
            audibleNow[x.RootPid] = (audibleNow.TryGetValue(x.RootPid, out var prev) && prev) || audible;
        }

        foreach (var (pid, r) in _roots)
        {
            // Roots absent from this snapshot count as silent.
            var audible = audibleNow.TryGetValue(pid, out var a) && a;
            if (r.LastSampleAt is null || at > r.LastSampleAt.Value)
            {
                r.LastSampleAt = at;
                r.Window.Enqueue((at, audible));
                while (r.Window.Count > 0 && r.Window.Peek().At <= at - attack) r.Window.Dequeue();
                if (audible)
                {
                    if (r.RunStart is null || r.LastAudibleAt is null || at - r.LastAudibleAt.Value > gapTolerance)
                        r.RunStart = at;
                    r.LastAudibleAt = at;
                }
                else if (r.LastAudibleAt is not null && at - r.LastAudibleAt.Value > gapTolerance)
                {
                    r.RunStart = null;
                }
            }

            r.IsGame = isGame(pid);
            if (!r.Playing)
            {
                if (r.RunStart is null || !audible) continue;
                int total = r.Window.Count, hits = r.Window.Count(w => w.Audible);
                if (total == 0 || hits < _t.AttackRatio * total) continue;
                bool recentlyPlaying = r.ReleasedAt is not null && at - r.ReleasedAt.Value <= TimeSpan.FromMilliseconds(_t.ReleaseMs);
                var needed = isForeground(pid) || recentlyPlaying ? _t.AttackMs : Math.Max(_t.AttackMs, _t.BurstIgnoreMs);
                if (at - r.RunStart.Value < TimeSpan.FromMilliseconds(needed)) continue;
                r.Playing = true;
                r.AudibleSince = r.RunStart;
            }
            else if (r.LastAudibleAt is not null && now - r.LastAudibleAt.Value >= Release(r))
            {
                r.Playing = false;
                r.ReleasedAt = now;
                r.AudibleSince = null;
            }
        }

        // Forget roots that are silent, released and old (keeps the dictionary small).
        var forgetAfter = TimeSpan.FromMilliseconds(Math.Max(_t.ReleaseMs, _t.GameReleaseMs) * 2.0);
        foreach (var pid in _roots.Where(kv => !kv.Value.Playing
                     && (kv.Value.LastAudibleAt is null || now - kv.Value.LastAudibleAt.Value > forgetAfter)
                     && !audibleNow.ContainsKey(kv.Key)).Select(kv => kv.Key).ToList())
            _roots.Remove(pid);

        return _roots.Select(kv => new PlayingState(kv.Key, kv.Value.Exe, kv.Value.Playing,
            kv.Value.Playing ? kv.Value.AudibleSince : null, kv.Value.LastAudibleAt)).ToList();
    }

    /// <summary>Next release deadline (null = nothing pending).</summary>
    public DateTimeOffset? NextDueAt
    {
        get
        {
            DateTimeOffset? next = null;
            foreach (var r in _roots.Values)
            {
                if (!r.Playing || r.LastAudibleAt is null) continue;
                var due = r.LastAudibleAt.Value + Release(r);
                if (next is null || due < next) next = due;
            }
            return next;
        }
    }

    private TimeSpan Release(Root r) => TimeSpan.FromMilliseconds(r.IsGame ? _t.GameReleaseMs : _t.ReleaseMs);
}
