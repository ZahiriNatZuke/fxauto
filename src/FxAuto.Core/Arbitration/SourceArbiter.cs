using System.Globalization;
using FxAuto.Core.Classification;
using FxAuto.Core.Config;
using FxAuto.Core.Localization;

namespace FxAuto.Core.Arbitration;

/// <summary>REQ-104 level-based arbiter with global hysteresis (plan "Key algorithms" 5). First level with
/// candidates wins: Locked/Editing (mode) → playing <c>hold</c> source (freeze) → foreground game playing → foreground
/// game loading (never sounded, process younger than <c>GameLoadingMaxMs</c>) → other foreground source playing →
/// background game playing → background source that started playing most recently → nothing playing: keep the
/// current winner for <c>IdleGraceMs</c>, then the device default. <c>call</c> never wins with
/// <see cref="CallPolicy.Ignore"/>. A change needs <c>MinDwellMs</c> since the previous change, except a foreground
/// game. An <c>unknown</c> background newcomer does not displace a classified background winner.
/// <see cref="ArbiterDecision.Reason"/> never contains a window title. Not thread-safe: coordinator thread only.</summary>
public sealed class SourceArbiter
{
    private readonly ArbiterTimings _t;
    private readonly IClock _clock;

    private CandidateSource? _current;               // winner that drives the preset now (null = device default)
    private WinLevel _currentLevel = WinLevel.IdleDefault;
    private DateTimeOffset? _lastChangeAt;
    private DateTimeOffset? _idleSince;
    private DateTimeOffset? _nextDue;

    public SourceArbiter(ArbiterTimings t, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(t);
        ArgumentNullException.ThrowIfNull(clock);
        _t = t;
        _clock = clock;
    }

    /// <summary>Arbiter timings from the general settings (config.json).</summary>
    public static ArbiterTimings TimingsFrom(GeneralSettings g)
    {
        ArgumentNullException.ThrowIfNull(g);
        return new ArbiterTimings(g.GameLoadingMaxMs, checked(g.IdleGraceSeconds * 1000), g.MinDwellMs, g.CallPolicy);
    }

    public ArbiterDecision Decide(IReadOnlyList<CandidateSource> sources, ForegroundInfo? fg, Mode mode)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var now = _clock.Now;
        _nextDue = null;

        switch (mode)
        {
            case Mode.Locked: return new ArbiterDecision(null, WinLevel.Locked, "locked by the user") { ReasonText = new Msg("Why.LockedByUser") };
            case Mode.Editing: return new ArbiterDecision(null, WinLevel.Editing, "editing a preset in FxSound") { ReasonText = new Msg("Why.EditingPreset") };
            case Mode.Paused: return new ArbiterDecision(null, WinLevel.Locked, "paused by the user") { ReasonText = new Msg("Why.PausedByUser") };
        }

        var cands = sources.Where(c => c.Class is not null
            && !(_t.CallPolicy == CallPolicy.Ignore && c.Class.Category == Categories.Call)).ToList();

        var hold = cands.FirstOrDefault(c => c.Playing && c.Class.Category == Categories.Hold);
        if (hold is not null)
            return new ArbiterDecision(hold, WinLevel.Hold, "hold source playing: changes frozen" + Src(hold)) { ReasonText = new Msg("Why.Hold", SrcText(hold)) };

        var normal = cands.Where(c => c.Class.Category != Categories.Hold).ToList();
        var (winner, level) = Pick(normal, now);

        if (winner is null)
        {
            if (_current is null)
            {
                _idleSince = null;
                _currentLevel = WinLevel.IdleDefault;
                return new ArbiterDecision(null, WinLevel.IdleDefault, "nothing playing: device default") { ReasonText = new Msg("Why.Idle") };
            }
            _idleSince ??= now;
            var graceEnd = _idleSince.Value + TimeSpan.FromMilliseconds(_t.IdleGraceMs);
            if (now < graceEnd)
            {
                _nextDue = graceEnd;
                return new ArbiterDecision(_current, WinLevel.IdleGrace,
                    "nothing playing: keeping the current preset for the grace time" + Src(_current)) { ReasonText = new Msg("Why.IdleGrace", SrcText(_current)) };
            }
            _current = null;
            _currentLevel = WinLevel.IdleDefault;
            _lastChangeAt = now;
            _idleSince = null;
            return new ArbiterDecision(null, WinLevel.IdleDefault, "nothing playing after the grace time: device default") { ReasonText = new Msg("Why.IdleAfterGrace") };
        }

        _idleSince = null;
        if (level == WinLevel.ForegroundGameLoading && winner.ProcessStart is not null)
            Due(winner.ProcessStart.Value + TimeSpan.FromMilliseconds(_t.GameLoadingMaxMs));

        bool change = _current is null || _current.RootPid != winner.RootPid || _current.Class.Category != winner.Class.Category;
        bool exempt = level is WinLevel.ForegroundGamePlaying or WinLevel.ForegroundGameLoading || _lastChangeAt is null;
        if (change && !exempt)
        {
            var dwellEnd = _lastChangeAt!.Value + TimeSpan.FromMilliseconds(_t.MinDwellMs);
            if (now < dwellEnd)
            {
                Due(dwellEnd);
                // keep the previous winner, refreshed if it is still in the list
                if (_current is not null)
                    _current = sources.FirstOrDefault(c => c.RootPid == _current.RootPid && c.Class.Category == _current.Class.Category) ?? _current;
                return new ArbiterDecision(_current, _currentLevel,
                    "minimum dwell: keeping the previous decision (" + LevelText(level) + " waits)" + Src(_current))
                { ReasonText = new Msg("Why.Dwell", LevelMsg(level), SrcText(_current)) };
            }
        }

        if (change) _lastChangeAt = now;
        _current = winner;
        _currentLevel = level;
        return new ArbiterDecision(winner, level, LevelText(level) + Src(winner)) { ReasonText = new Msg("Why.Winner", LevelMsg(level), SrcText(winner)) };
    }

    /// <summary>Grace end, dwell end or game-loading end (null = nothing pending).</summary>
    public DateTimeOffset? NextDueAt => _nextDue;

    private (CandidateSource? Winner, WinLevel Level) Pick(List<CandidateSource> c, DateTimeOffset now)
    {
        var fgGame = MostRecent(c.Where(x => x.IsForeground && x.Class.IsGame && x.Playing));
        if (fgGame is not null) return (fgGame, WinLevel.ForegroundGamePlaying);

        var loading = c.FirstOrDefault(x => x.IsForeground && x.Class.IsGame && !x.Playing && x.LastAudibleAt is null
            && x.ProcessStart is not null && now - x.ProcessStart.Value < TimeSpan.FromMilliseconds(_t.GameLoadingMaxMs));
        if (loading is not null) return (loading, WinLevel.ForegroundGameLoading);

        var fgPlaying = MostRecent(c.Where(x => x.IsForeground && x.Playing));
        if (fgPlaying is not null) return (fgPlaying, WinLevel.ForegroundPlaying);

        var bgGame = MostRecent(c.Where(x => !x.IsForeground && x.Class.IsGame && x.Playing));
        if (bgGame is not null) return (bgGame, WinLevel.BackgroundGamePlaying);

        var bg = c.Where(x => !x.IsForeground && x.Playing).ToList();
        if (bg.Count == 0) return (null, WinLevel.IdleDefault);

        // An unknown newcomer does not displace a classified background winner (ad autoplay, research 01 §5.3).
        var prev = _current is null ? null : bg.FirstOrDefault(x => x.RootPid == _current.RootPid);
        if (prev is not null && prev.Class.Category != Categories.Unknown)
            bg = bg.Where(x => x.RootPid == prev.RootPid || x.Class.Category != Categories.Unknown).ToList();

        var best = bg.OrderByDescending(x => x.AudibleSince ?? DateTimeOffset.MinValue)
            .ThenByDescending(x => Categories.BackgroundRank(x.Class.Category))
            .ThenByDescending(x => prev is not null && x.RootPid == prev.RootPid)
            .First();
        return (best, WinLevel.BackgroundMostRecent);
    }

    private static CandidateSource? MostRecent(IEnumerable<CandidateSource> xs)
        => xs.OrderByDescending(x => x.AudibleSince ?? DateTimeOffset.MinValue).FirstOrDefault();

    private void Due(DateTimeOffset at)
    {
        if (_nextDue is null || at < _nextDue) _nextDue = at;
    }

    private static string LevelText(WinLevel l) => l switch
    {
        WinLevel.ForegroundGamePlaying => "foreground game playing",
        WinLevel.ForegroundGameLoading => "foreground game loading",
        WinLevel.ForegroundPlaying => "foreground source playing",
        WinLevel.BackgroundGamePlaying => "background game playing",
        WinLevel.BackgroundMostRecent => "background source that started playing most recently",
        _ => l.ToString(),
    };

    private static Msg LevelMsg(WinLevel l) => new("Why.Level." + l);

    /// <summary>UI text of <see cref="Src"/>: category name and configured source id, never a title.</summary>
    private static Msg SrcText(CandidateSource? c)
        => c is null ? new Msg("Why.NoSrc") : c.Class.SourceId is null ? new Msg("Why.Src", new Msg("Category." + c.Class.Category))
            : new Msg("Why.SrcWithSource", new Msg("Category." + c.Class.Category), c.Class.SourceId);

    // Category and configured source id only: never a window title (privacy).
    private static string Src(CandidateSource? c)
        => c is null ? "" : string.Format(CultureInfo.InvariantCulture, " [{0}{1}]", c.Class.Category,
            c.Class.SourceId is null ? "" : ", source " + c.Class.SourceId);
}
