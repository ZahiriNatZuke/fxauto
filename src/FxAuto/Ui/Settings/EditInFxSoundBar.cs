using FxAuto.Core.Presets;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>Bar shown while a preset is being edited in FxSound (REQ-107): automation is paused until the user
/// picks Save, Save as new or Discard.</summary>
internal sealed class EditInFxSoundBar : Panel
{
    private readonly InfoBar _bar = new() { Tone = Tone.Info, Dock = DockStyle.Top, Margin = Padding.Empty };
    private string? _preset;

    public EditInFxSoundBar(SettingsForm window)
    {
        Margin = new Padding(0, 0, 0, Theme.S3);
        Padding = Padding.Empty;
        _bar.Palette = window.Palette;
        _bar.AddAction(Loc.T("EditBar.Save"), () => _ = window.FinishEditAsync(EditOutcome.Save));
        _bar.AddAction(Loc.T("EditBar.SaveAsNew"), () => _ = window.FinishEditAsync(EditOutcome.SaveAsNew));
        _bar.AddAction(Loc.T("EditBar.Discard"), () => _ = window.FinishEditAsync(EditOutcome.Discard));
        Controls.Add(_bar);
        Preset = null;
        _bar.SizeChanged += (_, _) => Height = _bar.Height;
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (Height != _bar.Height) Height = _bar.Height;
    }

    public override Size GetPreferredSize(Size proposedSize) => new(proposedSize.Width, _bar.Height);

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string? Preset
    {
        get => _preset;
        set
        {
            _preset = value;
            _bar.Message = Loc.F("EditBar.Message", value ?? Loc.T("EditBar.APreset"));
            AccessibleName = _bar.Message;
        }
    }
}
