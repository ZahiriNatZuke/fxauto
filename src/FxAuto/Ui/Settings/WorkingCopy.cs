using System.Text.Json;
using FxAuto.Core.Config;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>Top-level blocks of config.json the window rebases independently.</summary>
internal enum ConfigBlock { General, Presets, Devices, Sources, Assignments, Lists }

/// <summary>Result of rebasing the working copy onto a newer live config.</summary>
internal sealed record RebaseResult(FxAutoConfig Working, IReadOnlyList<ConfigBlock> ChangedOutside,
    IReadOnlyList<ConfigBlock> KeptLocal);

/// <summary>The window's working copy over a <see cref="VersionedConfig"/> (UI thread only).</summary>
internal sealed class WorkingCopy
{
    public WorkingCopy(VersionedConfig live)
    {
        Base = live;
        Working = live.Config;
    }

    /// <summary>The live config the working copy started from.</summary>
    public VersionedConfig Base { get; private set; }

    public FxAutoConfig Working { get; private set; }

    public IReadOnlyList<ConfigBlock> Dirty => Blocks.Where(b => Json(Working, b) != Json(Base.Config, b)).ToList();

    public bool IsDirty => Dirty.Count > 0;

    public void Edit(Func<FxAutoConfig, FxAutoConfig> change) => Working = change(Working);

    /// <summary>Cancel: back to the live config.</summary>
    public void Discard() => Working = Base.Config;

    /// <summary>Takes <paramref name="next"/> as the new base. Blocks the user has not edited follow the newer
    /// version; edited blocks keep the user's version.</summary>
    public RebaseResult Rebase(VersionedConfig next)
    {
        // "changed outside" = the newer version differs from what the user had and from what the user sees now
        // (a block that already equals the working copy, e.g. our own save, is not news)
        var changed = Blocks.Where(b => Json(Base.Config, b) != Json(next.Config, b) && Json(Working, b) != Json(next.Config, b)).ToList();
        var dirty = Dirty;
        var merged = next.Config;
        foreach (var b in dirty) merged = Take(merged, Working, b);
        Base = next;
        Working = merged;
        return new RebaseResult(merged, changed, dirty.Intersect(changed).ToList());
    }

    /// <summary>Saved: working copy and base are the same live config.</summary>
    public void Reset(VersionedConfig live)
    {
        Base = live;
        Working = live.Config;
    }

    public static readonly IReadOnlyList<ConfigBlock> Blocks = Enum.GetValues<ConfigBlock>();

    public static string Name(ConfigBlock b) => b switch
    {
        ConfigBlock.General => Loc.T("Block.General"),
        ConfigBlock.Presets => Loc.T("Block.Presets"),
        ConfigBlock.Devices => Loc.T("Block.Devices"),
        ConfigBlock.Sources => Loc.T("Block.Sources"),
        ConfigBlock.Assignments => Loc.T("Block.Assignments"),
        _ => Loc.T("Block.Lists"),
    };

    private static FxAutoConfig Take(FxAutoConfig into, FxAutoConfig from, ConfigBlock b) => b switch
    {
        ConfigBlock.General => into with { General = from.General },
        ConfigBlock.Presets => into with { Presets = from.Presets },
        ConfigBlock.Devices => into with { Devices = from.Devices },
        ConfigBlock.Sources => into with { Sources = from.Sources },
        ConfigBlock.Assignments => into with { Assignments = from.Assignments },
        _ => into with { IgnoredApps = from.IgnoredApps, Launchers = from.Launchers, GameFolderExclusions = from.GameFolderExclusions },
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Structural fingerprint of one block (records hold lists, so record equality is by reference).</summary>
    internal static string Json(FxAutoConfig c, ConfigBlock b) => b switch
    {
        ConfigBlock.General => JsonSerializer.Serialize(c.General, JsonOptions),
        ConfigBlock.Presets => JsonSerializer.Serialize(c.Presets.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList(), JsonOptions),
        ConfigBlock.Devices => JsonSerializer.Serialize(c.Devices, JsonOptions),
        ConfigBlock.Sources => JsonSerializer.Serialize(c.Sources, JsonOptions),
        ConfigBlock.Assignments => JsonSerializer.Serialize(c.Assignments, JsonOptions),
        _ => JsonSerializer.Serialize(new[] { c.IgnoredApps, c.Launchers, c.GameFolderExclusions }, JsonOptions),
    };
}
