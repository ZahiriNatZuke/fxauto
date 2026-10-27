using FxAuto.App;

namespace FxAuto.Ui.Settings;

/// <summary>Opens the settings window on demand (UI thread only). One window at a time: a second Open brings the
/// existing one forward and switches page. The window is disposed when it closes.</summary>
internal sealed class SettingsWindowOpener : ISettingsWindowOpener, IDisposable
{
    private readonly ISettingsHost _host;
    private SettingsForm? _form;
    private bool _disposed;

    public SettingsWindowOpener(ISettingsHost host) => _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>True while the window exists (for tests and the tray).</summary>
    public bool IsOpen => _form is { IsDisposed: false };

    public void Open(SettingsPage page, string? focusId = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_form is { IsDisposed: false } existing)
        {
            existing.ShowPage(page, focusId);
            existing.BringToFrontByUser();
            return;
        }

        var form = new SettingsForm(_host);
        form.ReopenRequested += p =>
        {
            if (!ReferenceEquals(_form, form) || _disposed) return;
            form.CloseForReopen();
            if (form.IsDisposed || !form.Visible) Open(p);
        };
        form.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(_form, form)) _form = null;
            form.Dispose();
        };
        _form = form;
        form.ShowPage(page, focusId);
        form.Show();
        form.Activate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var form = _form;
        _form = null;
        if (form is { IsDisposed: false })
        {
            form.Close();
            form.Dispose();
        }
    }
}
