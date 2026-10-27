using FxAuto.Core.Config;
using FxAuto.Core.Localization;

namespace FxAuto.Ui.Settings;

/// <summary>Applies the user's language and theme (<c>general.language</c>, <c>general.theme</c>) to the UI: the text
/// catalog and WinForms' colour mode, which <see cref="Theme.Current"/> follows. UI thread.</summary>
internal static class UiPreferences
{
    /// <summary>Language and theme last applied (to tell whether a config change touches them).</summary>
    public static (string Language, string Theme) Applied { get; private set; } = ("", "");

    /// <summary>Applies both; returns true when the resolved language or colour mode changed.</summary>
    public static bool Apply(GeneralSettings general)
    {
        ArgumentNullException.ThrowIfNull(general);
        var language = Loc.ResolveForThisMachine(general.Language);
        var mode = ColorModeOf(general.Theme);
        var changed = language != Loc.Language || mode != Application.ColorMode;
        Loc.SetLanguage(language);
        if (mode != Application.ColorMode) Application.SetColorMode(mode);
        Applied = (general.Language, general.Theme);
        return changed;
    }

    /// <summary>"light" → Classic, "dark" → Dark, anything else → System (Windows app mode).</summary>
    public static SystemColorMode ColorModeOf(string? theme) => theme?.Trim().ToLowerInvariant() switch
    {
        UiChoices.Light => SystemColorMode.Classic,
        UiChoices.Dark => SystemColorMode.Dark,
        _ => SystemColorMode.System,
    };
}
