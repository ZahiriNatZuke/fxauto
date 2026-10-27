using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using FxAuto.App;
using FxAuto.Core.Config;
using FxAuto.Core.Localization;
using FxAuto.Core.Updates;

namespace FxAuto.Ui.Settings;

/// <summary>About: FxAuto's version, author, updates (check, release notes, download with progress, verified install),
/// the FxSound and .NET versions in use, and support helpers (copy the versions, open the log folder). Nothing here
/// edits the working copy.</summary>
internal sealed class AboutPage : SettingsPageBase
{
    private static string Description => Loc.T("About.Description");
    private const string Author = "Yohan González Almaguer";

    private readonly Label _version = Ui.Text("", Theme.Caption);
    private readonly Label _fxVersion = Ui.Text("");
    private readonly Label _netVersion = Ui.Text("");

    public AboutPage(SettingsForm window) : base(window)
    {
        _updates = Host.Updates;

        // header: big icon next to name, version and the one-line description
        var icon = new IconView { Size = new Size(64, 64), Margin = new Padding(0, Theme.S1, Theme.S4, 0) };
        var name = Ui.Text("FxAuto", Theme.Title);
        name.Margin = Padding.Empty;
        _version.Margin = new Padding(0, 0, 0, Theme.S2);
        var description = Ui.Text(Description, Theme.Body, wrap: true, maxWidth: 560);
        description.Margin = Padding.Empty;
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        titles.Controls.AddRange([name, _version, description]);
        var header = new FlowLayoutPanel { WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, Theme.S2, 0, Theme.S3) };
        header.Controls.AddRange([icon, titles]);

        var by = Ui.Text(Loc.F("About.DevelopedBy", Author));
        by.Margin = new Padding(0, Theme.S1, 0, Theme.S1);
        var copyright = Ui.Text("© 2026 " + Author, Theme.Caption);
        copyright.Margin = new Padding(0, 0, 0, Theme.S2);

        var system = Ui.Grid(LabelColumn);
        Ui.AddField(system, "FxSound", _fxVersion);
        Ui.AddField(system, ".NET", _netVersion);

        var buttons = Ui.Row(Ui.Button(Loc.T("About.Copy"), CopyDetails, Loc.T("About.CopyAccessible")),
            Ui.Button(Loc.T("About.OpenLogs"), OpenLogFolder));
        buttons.WrapContents = false; // one line; wrapping made the row reserve a second, empty line
        buttons.Margin = new Padding(0, Theme.S1, 0, Theme.S2);

        var notice = Ui.Hint(Loc.T("About.Notice"), 560);
        notice.Margin = new Padding(0, Theme.S1, 0, Theme.S1);

        var stack = Stack(4);
        stack.Controls.Add(CardOf(header, by, copyright), 0, 0);
        stack.Controls.Add(BuildUpdatesCard(), 0, 1);
        stack.Controls.Add(CardOf(Ui.SectionTitle(Loc.T("About.Versions")), system, buttons), 0, 2);
        stack.Controls.Add(notice, 0, 3);
        Controls.Add(Scrolling(stack));
    }

    public override SettingsPage Page => SettingsPage.About;

    public override string Heading => Loc.T("Nav.About");

    private const int LabelColumn = 120;

    public override void Bind()
    {
        _version.Text = Loc.F("About.Version", FxAutoVersion());
        _fxVersion.Text = FxSoundVersion() ?? Loc.T("About.NotFound");
        _netVersion.Text = Environment.Version.ToString();
        BindUpdates();
    }

    /// <summary>"1.0.0 (d1317da)" from the informational version ("1.0.0+&lt;commit&gt;"), else the assembly version.</summary>
    internal static string FxAutoVersion(Assembly? assembly = null)
    {
        assembly ??= typeof(AboutPage).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info)) return assembly.GetName().Version?.ToString(3) ?? "unknown";
        var plus = info.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0) return info;
        var commit = info[(plus + 1)..];
        return commit.Length == 0 ? info[..plus] : $"{info[..plus]} ({commit[..Math.Min(7, commit.Length)]})";
    }

    /// <summary>Product version of the FxSound.exe FxAuto uses (General → FxSound location); null when it is not there.</summary>
    private string? FxSoundVersion()
    {
        var path = Host.Config.Config.General.FxSoundPath ?? DefaultConfig.FxSoundPath;
        try
        {
            if (!File.Exists(path)) return null;
            var v = FileVersionInfo.GetVersionInfo(path);
            var text = (v.ProductVersion ?? v.FileVersion)?.Trim();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private void CopyDetails()
    {
        var details = string.Join(Environment.NewLine,
            $"FxAuto {FxAutoVersion()}",
            $"FxSound {FxSoundVersion() ?? "not found"}",
            $".NET {Environment.Version}",
            $"Windows {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture})");
        try
        {
            Clipboard.SetText(details);
            Window.ShowMessage("about", Tone.Success, Loc.T("About.Copied"));
        }
        catch (ExternalException)
        {
            Window.ShowMessage("about", Tone.Warning, Loc.T("About.ClipboardBusy"));
        }
    }

    private void OpenLogFolder()
    {
        var folder = Host.LogFolder;
        if (!Directory.Exists(folder))
        {
            Window.ShowMessage("about", Tone.Warning, Loc.F("About.NoLogFolder", folder));
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Window.ShowMessage("about", Tone.Error, Loc.F("About.OpenFailed", ex.Message));
        }
    }

    // ───────────────────────── updates ─────────────────────────

    private const int UpdatesLabelColumn = 180;
    private const int NotesWidth = 560;

    private readonly IUpdateController? _updates;
    private readonly Label _installed = Ui.Text("");
    private readonly Label _updateStatus = Ui.Text("", Theme.Body, wrap: true, maxWidth: 400);
    private readonly Label _lastChecked = Ui.Text("");
    private readonly Label _releaseTitle = Ui.Text("", Theme.Section, wrap: true, maxWidth: NotesWidth);
    private readonly TextBox _notes = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        WordWrap = true,
        Width = NotesWidth,
        Height = 150,
        Font = Theme.Body,
    };
    private readonly LinkLabel _releaseLink = new() { AutoSize = true, Font = Theme.Body, Margin = new Padding(0, Theme.S2, 0, 0), UseMnemonic = false };
    private readonly FlowLayoutPanel _details = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, Theme.S2, 0, 0) };
    private readonly ProgressTrack _progress = new() { Width = 320, Height = 6, Margin = new Padding(0, 13, Theme.S3, 0) };
    private readonly Label _progressText = Ui.Text("");
    private readonly FlowLayoutPanel _progressRow = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, Theme.S2, 0, 0) };
    private Button _check = null!;
    private Button _updateNow = null!;
    private Button _later = null!;
    private FlowLayoutPanel _buttons = null!;
    private bool _checking;
    private bool _downloading;
    private bool _autoChecked;
    private SemVersion? _dismissed;
    private CancellationTokenSource? _downloadCts;

    private Card BuildUpdatesCard()
    {
        var grid = Ui.Grid(UpdatesLabelColumn);
        Ui.AddField(grid, Loc.T("Updates.Installed"), _installed);
        Ui.AddField(grid, Loc.T("Updates.Status"), _updateStatus);
        Ui.AddField(grid, Loc.T("Updates.LastChecked"), _lastChecked);

        _notes.AccessibleName = Loc.T("Updates.Notes");
        _releaseTitle.Margin = new Padding(0, Theme.S1, 0, Theme.S2);
        _releaseLink.Text = Loc.T("Updates.ReleasePage");
        _releaseLink.LinkClicked += (_, _) => OpenReleasePage();
        var notesField = Ui.Field(_notes);
        notesField.Margin = Padding.Empty;
        _details.Controls.AddRange([_releaseTitle, notesField, _releaseLink]);

        _progress.AccessibleName = Loc.T("Updates.ProgressAccessible");
        _progressText.Margin = new Padding(0, 6, Theme.S2, 0);
        var cancel = Ui.Button(Loc.T("Updates.Cancel"), () => _downloadCts?.Cancel());
        _progressRow.Controls.AddRange([_progress, _progressText, cancel]);

        _check = Ui.Button(Loc.T("Updates.Check"), () => _ = CheckNowAsync());
        _updateNow = new AccentButton
        {
            Text = Loc.T("Updates.UpdateNow"), AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(96, 32),
            Padding = new Padding(Theme.S2, 0, Theme.S2, 0), Margin = new Padding(0, 0, Theme.S2, 0), Font = Theme.Body, UseMnemonic = false,
        };
        _updateNow.Click += (_, _) => _ = UpdateNowAsync();
        _later = Ui.Button(Loc.T("Updates.Later"), Later);
        var buttons = _buttons = Ui.Row(_updateNow, _later, _check);
        buttons.WrapContents = false;
        buttons.Margin = new Padding(0, Theme.S2, 0, Theme.S2);

        _details.Visible = _progressRow.Visible = false;
        _updateNow.Visible = _later.Visible = false;
        if (_updates is null) _check.Enabled = false;
        else _updates.CheckCompleted += OnCheckCompleted;
        return CardOf(Ui.SectionTitle(Loc.T("Updates.Title")), grid, _details, _progressRow, buttons);
    }

    /// <summary>Raised on a pool thread (UpdateService): hop to the UI thread.</summary>
    private void OnCheckCompleted(UpdateCheckResult result)
    {
        try
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke(RefreshUpdates);
        }
        catch (InvalidOperationException) { }
    }

    private void BindUpdates()
    {
        _releaseLink.LinkColor = _releaseLink.ActiveLinkColor = _releaseLink.VisitedLinkColor = P.Accent;
        RefreshUpdates();
        // a version seen by an earlier run, but no check yet in this one: fetch its notes once
        if (!_autoChecked && _updates is { LastResult: null, PendingVersion: not null })
        {
            _autoChecked = true;
            _ = CheckNowAsync();
        }
    }

    private void RefreshUpdates()
    {
        if (IsDisposed) return;
        _installed.Text = FxAutoVersion();
        if (_updates is null)
        {
            _updateStatus.Text = Loc.T("Updates.Unavailable");
            _lastChecked.Text = Loc.T("Updates.Never");
            return;
        }
        var r = _updates.LastResult;
        _updateStatus.Text = _checking ? Loc.T("Updates.Checking")
            : _downloading ? Loc.T("Updates.Downloading")
            : StatusText(r, _updates.PendingVersion);
        _lastChecked.Text = _updates.LastCheckAt is { } at
            ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : Loc.T("Updates.Never");

        var showDetails = r is { Kind: UpdateCheckKind.UpdateAvailable, Latest: { } latest } && latest != _dismissed;
        if (showDetails)
        {
            _releaseTitle.Text = string.IsNullOrWhiteSpace(r!.Title) ? "FxAuto " + r.Latest : r.Title.Trim();
            var notes = UpdateNotice.PlainText(r.Notes);
            _notes.Text = notes.Length == 0 ? Loc.T("Updates.NoNotes") : notes;
            _releaseLink.Visible = !string.IsNullOrEmpty(r.ReleaseUrl);
        }
        _details.Visible = showDetails;
        _progressRow.Visible = _downloading;
        _updateNow.Visible = _later.Visible = showDetails && !_downloading;
        // primary action first (the flow panel can move a control that was hidden while the page was laid out)
        _buttons.Controls.SetChildIndex(_updateNow, 0);
        _buttons.Controls.SetChildIndex(_later, 1);
        _updateNow.Enabled = !_checking;
        _check.Enabled = !_checking && !_downloading;
    }

    /// <summary>Status line: up to date / version X is available / couldn't check (why).</summary>
    internal static string StatusText(UpdateCheckResult? r, SemVersion? pending) => r?.Kind switch
    {
        UpdateCheckKind.UpToDate => Loc.T("Updates.UpToDate"),
        UpdateCheckKind.UpdateAvailable => Loc.F("Updates.Available", r.Latest),
        UpdateCheckKind.Error => Loc.F("Updates.CheckFailed", CheckErrorText(r)),
        _ => pending is not null ? Loc.F("Updates.Available", pending) : Loc.T("Updates.NotChecked"),
    };

    internal static string CheckErrorText(UpdateCheckResult r) => r.ErrorKind switch
    {
        UpdateCheckError.RateLimited when r.RetryAfter is { } until =>
            Loc.F("Updates.Err.RateLimited", until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)),
        UpdateCheckError.Timeout => Loc.T("Updates.Err.Timeout"),
        UpdateCheckError.Network => Loc.T("Updates.Err.Network"),
        UpdateCheckError.Http or UpdateCheckError.RateLimited => Loc.F("Updates.Err.Http", r.HttpStatus?.ToString(CultureInfo.InvariantCulture) ?? "?"),
        UpdateCheckError.BadAnswer => Loc.T("Updates.Err.BadAnswer"),
        _ => Loc.T("Updates.Err.Other"),
    };

    internal static string DownloadErrorText(DownloadResult d) => d.Failure switch
    {
        DownloadFailure.Cancelled => Loc.T("Updates.Err.Cancelled"),
        DownloadFailure.NoUpdate => Loc.T("Updates.Err.NoUpdate"),
        DownloadFailure.Untrusted => Loc.T("Updates.Err.Untrusted"),
        DownloadFailure.NoChecksum => Loc.T("Updates.Err.NoChecksum"),
        DownloadFailure.Mismatch => Loc.T("Updates.Err.Mismatch"),
        _ => Loc.T("Updates.Err.Download"),
    };

    private async Task CheckNowAsync()
    {
        if (_updates is null || _checking || _downloading) return;
        _checking = true;
        Window.DismissMessage("updates");
        RefreshUpdates();
        try
        {
            await _updates.CheckAsync();
            _dismissed = null;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _checking = false;
            RefreshUpdates();
        }
    }

    private async Task UpdateNowAsync()
    {
        if (_updates?.LastResult is not { Kind: UpdateCheckKind.UpdateAvailable } r || _downloading) return;
        _downloading = true;
        var cts = _downloadCts = new CancellationTokenSource();
        Window.DismissMessage("updates");
        SetProgress(new DownloadProgress(0, r.Installer?.Size ?? 0));
        RefreshUpdates();
        DownloadResult d;
        try
        {
            d = await _updates.DownloadAndVerifyAsync(r, new Progress<DownloadProgress>(SetProgress), cts.Token);
        }
        catch (OperationCanceledException)
        {
            d = new DownloadResult(false, r.Latest, null, null, "Download cancelled.", Cancelled: true);
        }
        finally
        {
            _downloadCts = null;
            cts.Dispose();
        }
        if (IsDisposed) return;
        if (!d.Ok)
        {
            _downloading = false;
            RefreshUpdates();
            Window.ShowMessage("updates", d.Failure == DownloadFailure.Cancelled ? Tone.Warning : Tone.Error, DownloadErrorText(d));
            return;
        }
        Window.ShowMessage("updates", Tone.Info, Loc.T("Updates.Installing"));
        if (!_updates.LaunchInstallerAndExit(d))
        {
            _downloading = false;
            RefreshUpdates();
            Window.ShowMessage("updates", Tone.Error, Loc.T("Updates.Err.Launch"));
        }
    }

    private void SetProgress(DownloadProgress p)
    {
        if (IsDisposed) return;
        var culture = CultureInfo.GetCultureInfo(Loc.Language);
        var got = (p.BytesReceived / 1048576.0).ToString("0.0", culture);
        if (p.TotalBytes > 0)
        {
            _progress.Fraction = Math.Clamp((double)p.BytesReceived / p.TotalBytes, 0, 1);
            _progressText.Text = Loc.F("Updates.Progress", got, (p.TotalBytes / 1048576.0).ToString("0.0", culture));
        }
        else
        {
            _progress.Fraction = 0;
            _progressText.Text = Loc.F("Updates.ProgressUnknown", got);
        }
    }

    private void Later()
    {
        _dismissed = _updates?.LastResult?.Latest;
        RefreshUpdates();
        Window.ShowMessage("updates", Tone.Info, Loc.T("Updates.LaterHint"));
    }

    private void OpenReleasePage()
    {
        var url = _updates?.LastResult?.ReleaseUrl;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Window.ShowMessage("updates", Tone.Error, Loc.F("Updates.OpenFailed", ex.Message));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_updates is not null) _updates.CheckCompleted -= OnCheckCompleted;
            _downloadCts?.Cancel();
        }
        base.Dispose(disposing);
    }

    /// <summary>Thin progress bar drawn from the palette (the native one loses its track in dark mode).</summary>
    private sealed class ProgressTrack : Control
    {
        private double _fraction;

        public ProgressTrack()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            AccessibleRole = AccessibleRole.ProgressBar;
        }

        /// <summary>0..1.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double Fraction
        {
            get => _fraction;
            set
            {
                _fraction = Math.Clamp(value, 0, 1);
                AccessibleDescription = ((int)Math.Round(_fraction * 100)).ToString(CultureInfo.InvariantCulture) + " %";
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var p = Theme.Current;
            e.Graphics.Clear(Parent?.BackColor ?? p.Card);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var r = new RectangleF(0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            using (var track = Theme.Rounded(r, r.Height / 2))
            using (var b = new SolidBrush(p.CardBorder))
                e.Graphics.FillPath(b, track);
            if (_fraction <= 0) return;
            var f = new RectangleF(r.X, r.Y, Math.Max(r.Height, (float)(r.Width * _fraction)), r.Height);
            using var fill = Theme.Rounded(f, f.Height / 2);
            using var a = new SolidBrush(p.Accent);
            e.Graphics.FillPath(a, fill);
        }
    }

    /// <summary>The app icon drawn at the control's size (sharp at any DPI); takes the card colour behind it.</summary>
    private sealed class IconView : Panel
    {
        public IconView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            AccessibleRole = AccessibleRole.Graphic;
            AccessibleName = Loc.T("About.Icon");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var size = Math.Min(ClientSize.Width, ClientSize.Height);
            if (size > 0) AppIcon.Draw(e.Graphics, size, AppIcon.Verified, paused: false);
        }
    }
}
