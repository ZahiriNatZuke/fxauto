using FxAuto.Core.Config;
using FxAuto.Core.FxSound;

namespace FxAuto.Core.Presets;

/// <summary>REQ-107/108 preset operations as verified step lists (research 03 §3.3 A–D, spike 04 (c)), each run
/// inside one <see cref="FxSoundController.RunExclusiveAsync{T}"/> session.
/// Every command is followed by <c>--status</c>; a failed verification runs the procedure's recovery
/// (<c>--preset=X</c> + <c>--undo_preset</c> for a preset this operation modified; the previous output name for
/// <c>--output</c>). A name that fails <see cref="PresetNameRules"/> and an output name absent from
/// <c>output_devices</c> are never sent. Every procedure ends checking that no preset it touched is modified.</summary>
/// <remarks>The config side (presets map, defaults, assignments) is applied by the caller with
/// <see cref="ApplyToConfig"/> after a successful result; <see cref="CheckDelete"/> refuses deleting a device
/// default without a replacement (also enforced here when <see cref="CurrentConfig"/> is set).</remarks>
public sealed class PresetOperations
{
    /// <summary>Exclusive session budget per expected FxSound call (command or --status).</summary>
    public static readonly TimeSpan PerCall = TimeSpan.FromSeconds(15);
    public const string ClarityEffect = "clarity";
    /// <summary>Tolerance when comparing the DSP of a copy with its source.</summary>
    public const double DspTolerance = 0.05;

    private readonly FxSoundController _c;
    private readonly IPresetFileStore _files;
    private readonly IClock _clock;
    private readonly Action<string, object> _log;

    public PresetOperations(FxSoundController c, IPresetFileStore files, IClock clock, Action<string, object> log)
    {
        ArgumentNullException.ThrowIfNull(c);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);
        _c = c;
        _files = files;
        _clock = clock;
        _log = log;
    }

    /// <summary>Current live config, used to refuse deleting a device default without replacement. Optional.</summary>
    public Func<FxAutoConfig?>? CurrentConfig { get; init; }

    /// <summary>Wait after <c>--output</c> before verifying (research 02 §5.3: ~300 ms).</summary>
    public TimeSpan OutputSettle { get; init; } = TimeSpan.FromMilliseconds(300);

    public async Task<PresetOpResult> RunAsync(PresetOpRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var started = _clock.Now;
        PresetOpResult res;
        try
        {
            res = req.Kind switch
            {
                PresetOpKind.Duplicate => await Exclusive(14, (s, run) => DuplicateAsync(s, run, req), ct).ConfigureAwait(false),
                PresetOpKind.Rename => await Exclusive(10, (s, run) => RenameAsync(s, run, req.Source, req.Target), ct).ConfigureAwait(false),
                PresetOpKind.Delete => await DeleteGuarded(req, ct).ConfigureAwait(false),
                PresetOpKind.SwitchOutput => await Exclusive(6, (s, run) => SwitchOutputAsync(s, run, req), ct).ConfigureAwait(false),
                PresetOpKind.EditBegin => await Exclusive(4, (s, run) => EditBeginAsync(s, run, req), ct).ConfigureAwait(false),
                PresetOpKind.EditFinish => await Exclusive(10, (s, run) => EditFinishAsync(s, run, req), ct).ConfigureAwait(false),
                _ => Refused(new Run(), $"unknown operation {req.Kind}"),
            };
        }
        catch (TimeoutException ex)
        {
            res = new PresetOpResult(false, ex.Message, [], null, []) { ReapplyRequested = true };
        }
        _log("preset-op", new
        {
            kind = req.Kind.ToString(), source = req.Source, target = req.Target, ok = res.Ok, error = res.Error,
            steps = res.Steps.Count, ms = (long)(_clock.Now - started).TotalMilliseconds,
        });
        return res;
    }

    // ───────────── config side (pure) ─────────────

    /// <summary>Error if <paramref name="name"/> is the default preset of a device and no replacement is given.</summary>
    public static string? CheckDelete(FxAutoConfig cfg, string name, string? replacement)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(name);
        if (replacement is not null && string.Equals(replacement, name, StringComparison.Ordinal))
            return "The replacement is the preset being deleted.";
        if (!string.IsNullOrWhiteSpace(replacement)) return null;
        var users = cfg.Devices.Where(d => string.Equals(d.DefaultPreset, name, StringComparison.Ordinal)).Select(d => d.DisplayName).ToList();
        return users.Count == 0 ? null
            : $"\"{name}\" is the default preset of {string.Join(", ", users)}: choose a replacement first.";
    }

    /// <summary>Applies a successful operation to the config: duplicate adds the copy's metadata (volume leveling of
    /// the source), rename renames everywhere, delete removes it moving references to the replacement
    /// (<c>req.Target</c>). Other kinds return the config unchanged.</summary>
    /// <summary>
    /// Does this operation change FxAuto's config when it succeeds? Those must not start while the config
    /// can't be saved: FxSound would change and the config would keep pointing at the old names.
    /// </summary>
    public static bool ChangesConfig(PresetOpRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        return req.Kind is PresetOpKind.Duplicate or PresetOpKind.Rename or PresetOpKind.Delete
            || (req.Kind == PresetOpKind.EditFinish && req.Outcome == EditOutcome.SaveAsNew);
    }

    public static FxAutoConfig ApplyToConfig(FxAutoConfig cfg, PresetOpRequest req, PresetOpResult res)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(res);
        if (!res.Ok) return cfg;
        switch (req.Kind)
        {
            case PresetOpKind.Duplicate:
            case PresetOpKind.EditFinish when req.Outcome == EditOutcome.SaveAsNew:
            {
                var next = cfg;
                foreach (var (from, to) in res.Done)
                {
                    var meta = next.Presets.TryGetValue(from, out var m) ? m : new PresetMeta(0, null);
                    var presets = new Dictionary<string, PresetMeta>(next.Presets, StringComparer.Ordinal) { [to] = meta with { } };
                    next = next with { Presets = presets };
                }
                return next;
            }
            case PresetOpKind.Rename:
                return res.Done.Aggregate(cfg, (c, r) => ConfigEdits.RenamePresetEverywhere(c, r.From, r.To));
            case PresetOpKind.Delete:
                return res.Done.Aggregate(cfg, (c, r) => ConfigEdits.RemovePreset(c, r.From, string.IsNullOrEmpty(r.To) ? null : r.To));
            default:
                return cfg;
        }
    }

    // ───────────── procedures ─────────────

    /// <summary>Steps and outcome of one procedure.</summary>
    private sealed class Run
    {
        public List<string> Steps { get; } = [];
        public List<(string From, string To)> Done { get; } = [];
        public FxStatus? Last { get; set; }
        /// <summary>FxSound's active preset may have changed: the engine must re-apply.</summary>
        public bool SelectionChanged { get; set; }
    }

    private Task<PresetOpResult> Exclusive(int expectedCalls, Func<IFxSession, Run, Task<PresetOpResult>> body, CancellationToken ct)
    {
        var timeout = PerCall * expectedCalls;
        return _c.RunExclusiveAsync(s => body(s, new Run()), timeout, ct);
    }

    private static PresetOpResult Refused(Run run, string error)
        => new(false, error, run.Steps, run.Last, run.Done) { ReapplyRequested = run.SelectionChanged };

    private static PresetOpResult Failed(Run run, string error)
        => new(false, error, run.Steps, run.Last, run.Done) { ReapplyRequested = run.SelectionChanged };

    private static PresetOpResult Succeeded(Run run)
        => new(true, null, run.Steps, run.Last, run.Done) { ReapplyRequested = run.SelectionChanged };

    private async Task<bool> SendAsync(IFxSession s, Run run, string args, CancellationToken ct = default)
    {
        if (args.StartsWith("--preset=", StringComparison.Ordinal) || args.StartsWith("--save_preset=", StringComparison.Ordinal)
            || args.StartsWith("--rename_preset=", StringComparison.Ordinal) || args.StartsWith("--delete_preset", StringComparison.Ordinal))
            run.SelectionChanged = true;
        var exit = await s.SendAsync(args, ct).ConfigureAwait(false);
        run.Steps.Add(exit is null ? $"{args} → no answer" : $"{args} → exit {exit}");
        return exit is not null;
    }

    private static async Task<FxStatus?> QueryAsync(IFxSession s, Run run, CancellationToken ct = default)
    {
        var st = await s.QueryAsync(ct).ConfigureAwait(false);
        run.Steps.Add(st is null ? "--status → no answer" : $"--status → selected \"{st.SelectedPreset}\"");
        if (st is not null) run.Last = st;
        return st;
    }

    private static bool Exists(FxStatus st, string name)
        => st.UserPresets.Contains(name, StringComparer.Ordinal) || st.BuiltInPresets.Contains(name, StringComparer.Ordinal);

    private static bool IsUser(FxStatus st, string name) => st.UserPresets.Contains(name, StringComparer.Ordinal);

    private static bool IsModified(FxStatus st, string name)
        => st.ModifiedByPreset.TryGetValue(name, out var m) ? m
            : string.Equals(st.SelectedPreset, name, StringComparison.Ordinal) && st.SelectedModified == true;

    /// <summary>Common precondition: FxSound answers, processing is on, and the active preset has no unsaved changes
    /// (switching away from it would make FxSound auto-save them, research 04 (c) case 2).</summary>
    private static async Task<(FxStatus? St, string? Error)> StartAsync(IFxSession s, Run run, bool allowActiveModified = false)
    {
        var st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is null) return (null, "FxSound is closed or not responding.");
        if (!st.Power) return (st, "FxSound processing is off: turn it on first.");
        if (!allowActiveModified && st.SelectedPreset is { } active && IsModified(st, active))
            return (st, $"The active preset \"{active}\" has unsaved changes in FxSound: save or discard them first.");
        return (st, null);
    }

    /// <summary>Recovery of a preset this operation modified: <c>--preset=X</c> then <c>--undo_preset</c>, verified.
    /// Also clears an AutoSave copy that may already exist (spike 04 (c) case 2).</summary>
    private async Task<bool> RecoverAsync(IFxSession s, Run run, string preset)
    {
        run.Steps.Add($"recovery of \"{preset}\"");
        _log("preset-op-recovery", new { preset });
        await SendAsync(s, run, FxCommand.SelectPreset(preset)).ConfigureAwait(false);
        await SendAsync(s, run, FxCommand.UndoPreset()).ConfigureAwait(false);
        var st = await QueryAsync(s, run).ConfigureAwait(false);
        var ok = st is not null && !IsModified(st, preset) && !_files.AutoSaveExists(preset);
        if (!ok) run.Steps.Add($"recovery of \"{preset}\" not verified");
        return ok;
    }

    /// <summary>Removes a copy left by a failed duplicate: <c>--preset=Y</c> then <c>--delete_preset</c>.</summary>
    private async Task CleanupCopyAsync(IFxSession s, Run run, string copy)
    {
        run.Steps.Add($"cleanup of \"{copy}\"");
        await SendAsync(s, run, FxCommand.SelectPreset(copy)).ConfigureAwait(false);
        var st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is null || !string.Equals(st.SelectedPreset, copy, StringComparison.Ordinal)) return; // never delete another preset
        await SendAsync(s, run, FxCommand.DeletePreset()).ConfigureAwait(false);
        await QueryAsync(s, run).ConfigureAwait(false);
    }

    /// <summary>Final check: none of <paramref name="touched"/> is left modified or auto-saved; recovers those that are.</summary>
    private async Task<string?> FinalCheckAsync(IFxSession s, Run run, params string[] touched)
    {
        var st = run.Last;
        if (st is null) return "FxSound did not answer the final --status.";
        foreach (var p in touched.Where(p => Exists(st, p)).Distinct(StringComparer.Ordinal))
        {
            if (IsModified(st, p) || _files.AutoSaveExists(p))
            {
                if (!await RecoverAsync(s, run, p).ConfigureAwait(false))
                    return $"\"{p}\" is still modified in FxSound after the recovery.";
                st = run.Last!;
            }
        }
        return null;
    }

    // A. Duplicate X → Y (exact copy, without restarting FxSound)
    private async Task<PresetOpResult> DuplicateAsync(IFxSession s, Run run, PresetOpRequest req)
    {
        var x = req.Source;
        var y = req.Target;
        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)) return Refused(run, "Duplicate needs a source and a new name.");
        var (st, err) = await StartAsync(s, run).ConfigureAwait(false);
        if (err is not null) return Refused(run, err);
        if (!Exists(st!, x)) return Refused(run, $"\"{x}\" does not exist in FxSound.");
        if (IsModified(st!, x)) return Refused(run, $"\"{x}\" has unsaved changes in FxSound: save or discard them first.");
        var check = PresetNameRules.Validate(y, st!, _files.MaxUserPresets());
        if (!check.Ok) return Refused(run, check.Error!);
        var sourceFile = _files.FindPresetFile(x);
        if (sourceFile is null) return Refused(run, $"The preset file of \"{x}\" was not found.");

        // 2. select X
        if (!await SendAsync(s, run, FxCommand.SelectPreset(x)).ConfigureAwait(false))
            return Failed(run, await FailWithFinalAsync(s, run, "FxSound did not answer --preset.", x));
        st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is null || !string.Equals(st.SelectedPreset, x, StringComparison.Ordinal))
            return Failed(run, await FailWithFinalAsync(s, run, $"FxSound did not select \"{x}\".", x));
        var dspX = st.Dsp;
        if (dspX is null || !dspX.Effects.TryGetValue(ClarityEffect, out var clarity))
            return Failed(run, await FailWithFinalAsync(s, run, "status.json has no effects: cannot mark the preset as modified.", x));

        // 3. mark X modified with its own clarity value (inaudible); from here X must end clean
        await SendAsync(s, run, FxCommand.SetEffect(ClarityEffect, clarity)).ConfigureAwait(false);
        st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is null || !IsModified(st, x))
        {
            await RecoverAsync(s, run, x).ConfigureAwait(false);
            return Failed(run, $"FxSound did not mark \"{x}\" as modified.");
        }

        // 4. save as Y (FxSound clears X's AutoSave and selects Y)
        await SendAsync(s, run, FxCommand.SavePreset(y)).ConfigureAwait(false);
        st = await QueryAsync(s, run).ConfigureAwait(false);
        var yExists = st is not null && IsUser(st, y);
        if (st is null || !yExists || !string.Equals(st.SelectedPreset, y, StringComparison.Ordinal)
            || IsModified(st, x) || _files.AutoSaveExists(x))
        {
            var why = st is null ? "FxSound did not answer after --save_preset."
                : !yExists ? $"FxSound did not create \"{y}\"."
                : $"FxSound state after saving \"{y}\" is not the expected one.";
            if (yExists) await CleanupCopyAsync(s, run, y).ConfigureAwait(false);
            await RecoverAsync(s, run, x).ConfigureAwait(false);
            return Failed(run, why);
        }

        // 5. exact copy of X's file under Y's name (line 3), so Y is byte-identical to X
        var writeErr = _files.WriteExactCopy(sourceFile, y);
        run.Steps.Add(writeErr is null ? $"exact copy of \"{sourceFile}\" as \"{y}\"" : $"exact copy failed: {writeErr}");
        if (writeErr is not null)
        {
            await CleanupCopyAsync(s, run, y).ConfigureAwait(false);
            return Failed(run, await FailWithFinalAsync(s, run, $"Could not write the copy: {writeErr}", x, y));
        }

        // 6. reload Y from disk and compare with X
        await SendAsync(s, run, FxCommand.SelectPreset(y)).ConfigureAwait(false);
        st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is null || !string.Equals(st.SelectedPreset, y, StringComparison.Ordinal) || IsModified(st, y))
        {
            var why = st is null ? "FxSound did not answer after reloading the copy." : $"\"{y}\" did not reload cleanly.";
            return Failed(run, await FailWithFinalAsync(s, run, why, x, y));
        }
        var diff = DspDifference(dspX, st.Dsp);
        if (diff is not null)
        {
            await CleanupCopyAsync(s, run, y).ConfigureAwait(false);
            return Failed(run, await FailWithFinalAsync(s, run, $"The copy does not sound like \"{x}\": {diff}.", x, y));
        }

        var final = await FinalCheckAsync(s, run, x, y).ConfigureAwait(false);
        if (final is not null) return Failed(run, final);
        run.Done.Add((x, y));
        return Succeeded(run);
    }

    // B. Rename X → Y (user presets only)
    /// <summary>The preset FxSound had selected before a rename (or before the first of a bulk rename), carried
    /// through the renames so the selection can be put back at the end instead of leaving the renamed one active.</summary>
    private sealed class SelectionBefore
    {
        public bool Known { get; set; }
        public string? Preset { get; set; }
    }

    private async Task<PresetOpResult> RenameAsync(IFxSession s, Run run, string? x, string? y,
        SelectionBefore? before = null, bool restore = true)
    {
        if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y)) return Refused(run, "Rename needs the current and the new name.");
        var (st, err) = await StartAsync(s, run).ConfigureAwait(false);
        if (err is not null) return Refused(run, err);
        before ??= new SelectionBefore();
        if (!before.Known)
        {
            before.Known = true;
            before.Preset = st!.SelectedPreset;
        }
        if (!IsUser(st!, x))
            return Refused(run, Exists(st!, x) ? $"\"{x}\" is a built-in preset: it cannot be renamed." : $"\"{x}\" does not exist in FxSound.");
        if (IsModified(st!, x)) return Refused(run, $"\"{x}\" has unsaved changes in FxSound: save or discard them first.");
        var check = PresetNameRules.Validate(y, st!, _files.MaxUserPresets(), renamingFrom: x);
        if (!check.Ok) return Refused(run, check.Error!);

        await SendAsync(s, run, FxCommand.SelectPreset(x)).ConfigureAwait(false);
        st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is null || !string.Equals(st.SelectedPreset, x, StringComparison.Ordinal))
            return Failed(run, await FailWithFinalAsync(s, run, $"FxSound did not select \"{x}\".", x));

        await SendAsync(s, run, FxCommand.RenamePreset(y)).ConfigureAwait(false);
        st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is null || !IsUser(st, y) || Exists(st, x))
        {
            var why = st is null ? "FxSound did not answer after --rename_preset." : $"FxSound did not rename \"{x}\" to \"{y}\".";
            return Failed(run, await FailWithFinalAsync(s, run, why, x, y));
        }
        var final = await FinalCheckAsync(s, run, y).ConfigureAwait(false);
        if (final is not null) return Failed(run, final);
        run.Done.Add((x, y));
        if (string.Equals(before.Preset, x, StringComparison.Ordinal)) before.Preset = y;
        if (restore) await RestoreSelectionAsync(s, run, before.Preset).ConfigureAwait(false);
        return Succeeded(run);
    }

    /// <summary>Selects <paramref name="preset"/> again if it still exists and is not the active one. Best effort:
    /// the coordinator re-applies its expected preset afterwards anyway.</summary>
    private async Task RestoreSelectionAsync(IFxSession s, Run run, string? preset)
    {
        var st = run.Last;
        if (string.IsNullOrEmpty(preset) || st is null || !Exists(st, preset)
            || string.Equals(st.SelectedPreset, preset, StringComparison.Ordinal)) return;
        run.Steps.Add($"restore the selection \"{preset}\"");
        await SendAsync(s, run, FxCommand.SelectPreset(preset)).ConfigureAwait(false);
        await QueryAsync(s, run).ConfigureAwait(false);
    }

    // C. Delete X (user presets only); req.Target = replacement for the config references
    private Task<PresetOpResult> DeleteGuarded(PresetOpRequest req, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(req.Source) && CurrentConfig?.Invoke() is { } cfg
            && CheckDelete(cfg, req.Source, req.Target) is { } refuse)
            return Task.FromResult(Refused(new Run(), refuse));
        return Exclusive(8, (s, run) => DeleteAsync(s, run, req.Source, req.Target), ct);
    }

    private async Task<PresetOpResult> DeleteAsync(IFxSession s, Run run, string? x, string? replacement)
    {
        if (string.IsNullOrEmpty(x)) return Refused(run, "Delete needs a preset name.");
        var (st, err) = await StartAsync(s, run).ConfigureAwait(false);
        if (err is not null) return Refused(run, err);
        if (!IsUser(st!, x))
            return Refused(run, Exists(st!, x) ? $"\"{x}\" is a built-in preset: it cannot be deleted." : $"\"{x}\" does not exist in FxSound.");
        if (IsModified(st!, x)) return Refused(run, $"\"{x}\" has unsaved changes in FxSound: save or discard them first.");
        if (replacement is not null && replacement.Length > 0 && !Exists(st!, replacement))
            return Refused(run, $"The replacement \"{replacement}\" does not exist in FxSound.");

        await SendAsync(s, run, FxCommand.SelectPreset(x)).ConfigureAwait(false);
        st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is null || !string.Equals(st.SelectedPreset, x, StringComparison.Ordinal))
            return Failed(run, await FailWithFinalAsync(s, run, $"FxSound did not select \"{x}\": nothing was deleted.", x));

        await SendAsync(s, run, FxCommand.DeletePreset()).ConfigureAwait(false);
        st = await QueryAsync(s, run).ConfigureAwait(false);
        // FxSound selects another preset by itself (General): the result always asks for a re-apply
        run.SelectionChanged = true;
        if (st is null || Exists(st, x))
        {
            var why = st is null ? "FxSound did not answer after --delete_preset." : $"FxSound did not delete \"{x}\".";
            return Failed(run, await FailWithFinalAsync(s, run, why, x));
        }
        var final = await FinalCheckAsync(s, run, st.SelectedPreset ?? "").ConfigureAwait(false);
        if (final is not null) return Failed(run, final);
        run.Done.Add((x, replacement ?? ""));
        return Succeeded(run);
    }

    // Output switching (REQ-108): the exact listed name, alone, verified; restores the previous one on failure
    private async Task<PresetOpResult> SwitchOutputAsync(IFxSession s, Run run, PresetOpRequest req)
    {
        var name = req.OutputName;
        if (string.IsNullOrEmpty(name)) return Refused(run, "No output name.");
        var st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is null) return Refused(run, "FxSound is closed or not responding.");
        if (!st.OutputDevices.Contains(name, StringComparer.Ordinal))
            return Refused(run, $"\"{name}\" is not in FxSound's output list now.");
        if (string.Equals(st.SelectedOutput, name, StringComparison.Ordinal))
        {
            run.Steps.Add("already selected");
            return Succeeded(run);
        }
        var previous = st.SelectedOutput;

        await SendAsync(s, run, FxCommand.Output(name)).ConfigureAwait(false);
        // FxSound applies its own per-device preset on output change and the engine must re-decide for the new device
        run.SelectionChanged = true;
        if (OutputSettle > TimeSpan.Zero) await Task.Delay(OutputSettle).ConfigureAwait(false);
        st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is not null && string.Equals(st.SelectedOutput, name, StringComparison.Ordinal))
            return Succeeded(run);

        // restore the previous name so FxSound's saved output is not left on a name that did not take
        var why = st is null ? "FxSound did not answer after --output." : $"FxSound output is \"{st.SelectedOutput}\" instead of \"{name}\".";
        if (st is not null && previous is not null && st.OutputDevices.Contains(previous, StringComparer.Ordinal)
            && !string.Equals(st.SelectedOutput, previous, StringComparison.Ordinal))
        {
            run.Steps.Add($"restore output \"{previous}\"");
            await SendAsync(s, run, FxCommand.Output(previous)).ConfigureAwait(false);
            if (OutputSettle > TimeSpan.Zero) await Task.Delay(OutputSettle).ConfigureAwait(false);
            await QueryAsync(s, run).ConfigureAwait(false);
        }
        return Failed(run, why);
    }

    // D. "Edit in FxSound", begin: select P and show FxSound's window (the caller enters Mode.Editing)
    private async Task<PresetOpResult> EditBeginAsync(IFxSession s, Run run, PresetOpRequest req)
    {
        var p = req.Source;
        if (string.IsNullOrEmpty(p)) return Refused(run, "No preset to edit.");
        var (st, err) = await StartAsync(s, run).ConfigureAwait(false);
        if (err is not null) return Refused(run, err);
        if (!Exists(st!, p)) return Refused(run, $"\"{p}\" does not exist in FxSound.");
        await SendAsync(s, run, FxCommand.EditInFxSound(p)).ConfigureAwait(false);
        st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is null || !string.Equals(st.SelectedPreset, p, StringComparison.Ordinal))
            return Failed(run, st is null ? "FxSound did not answer." : $"FxSound did not select \"{p}\".");
        return Succeeded(run);
    }

    // D. "Edit in FxSound", finish: Save (--overwrite_preset), Save as new (--save_preset=Y) or Discard (--undo_preset).
    // The changes are the user's: a failed save is reported and nothing is discarded behind their back.
    private async Task<PresetOpResult> EditFinishAsync(IFxSession s, Run run, PresetOpRequest req)
    {
        var p = req.Source;
        if (string.IsNullOrEmpty(p) || req.Outcome is not EditOutcome outcome) return Refused(run, "Edit finish needs the preset and an outcome.");
        var (st, err) = await StartAsync(s, run, allowActiveModified: true).ConfigureAwait(false);
        if (err is not null) return Refused(run, err);
        if (!Exists(st!, p)) return Refused(run, $"\"{p}\" does not exist in FxSound any more.");
        if (!IsModified(st!, p) && !_files.AutoSaveExists(p))
        {
            run.Steps.Add("no unsaved changes");
            return Succeeded(run);
        }
        NameCheck? check = null;
        if (outcome == EditOutcome.SaveAsNew)
        {
            if (string.IsNullOrEmpty(req.Target)) return Refused(run, "Save as new needs a name.");
            check = PresetNameRules.Validate(req.Target, st!, _files.MaxUserPresets());
            if (!check.Ok) return Refused(run, check.Error!);
        }
        if (outcome == EditOutcome.Save && !IsUser(st!, p))
            return Refused(run, "Built-in presets cannot be overwritten: use Save as new.");

        if (!string.Equals(st!.SelectedPreset, p, StringComparison.Ordinal))
        {
            if (st.SelectedPreset is { } other && IsModified(st, other))
                return Refused(run, $"\"{other}\" is active in FxSound with unsaved changes: select \"{p}\" in FxSound first.");
            await SendAsync(s, run, FxCommand.SelectPreset(p)).ConfigureAwait(false);
            st = await QueryAsync(s, run).ConfigureAwait(false);
            if (st is null || !string.Equals(st.SelectedPreset, p, StringComparison.Ordinal))
                return Failed(run, $"FxSound did not select \"{p}\".");
        }

        var args = outcome switch
        {
            EditOutcome.Save => FxCommand.OverwritePreset(),
            EditOutcome.SaveAsNew => FxCommand.SavePreset(req.Target!),
            _ => FxCommand.UndoPreset(),
        };
        await SendAsync(s, run, args).ConfigureAwait(false);
        run.SelectionChanged = true;
        st = await QueryAsync(s, run).ConfigureAwait(false);
        if (st is null) return Failed(run, "FxSound did not answer after finishing the edit.");
        if (outcome == EditOutcome.SaveAsNew && !IsUser(st, req.Target!))
            return Failed(run, $"FxSound did not create \"{req.Target}\"; the changes are still unsaved in \"{p}\".");
        if (IsModified(st, p) || _files.AutoSaveExists(p))
            return Failed(run, $"\"{p}\" still shows unsaved changes in FxSound.");
        if (outcome == EditOutcome.SaveAsNew) run.Done.Add((p, req.Target!));
        return Succeeded(run);
    }

    /// <summary>Runs the final check after a failure (recovering any touched preset) and returns the error to report.</summary>
    private async Task<string> FailWithFinalAsync(IFxSession s, Run run, string error, params string[] touched)
    {
        await QueryAsync(s, run).ConfigureAwait(false);
        var final = await FinalCheckAsync(s, run, touched).ConfigureAwait(false);
        return final is null ? error : error + " " + final;
    }

    /// <summary>null if equal within <see cref="DspTolerance"/>; otherwise what differs.</summary>
    public static string? DspDifference(FxDsp? a, FxDsp? b)
    {
        if (a is null || b is null) return "DSP values not available";
        if (a.Bands.Count != b.Bands.Count) return $"{a.Bands.Count} bands vs {b.Bands.Count}";
        for (var i = 0; i < a.Bands.Count; i++)
        {
            if (Math.Abs(a.Bands[i] - b.Bands[i]) > DspTolerance) return $"band {i}";
        }
        if (a.MasterGain is double ma && b.MasterGain is double mb && Math.Abs(ma - mb) > DspTolerance) return "master gain";
        foreach (var (k, v) in a.Effects)
        {
            if (!b.Effects.TryGetValue(k, out var w) || Math.Abs(v - w) > DspTolerance) return $"effect {k}";
        }
        return null;
    }
}
