using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;

namespace UITests.Logic;

/// <summary>
/// Fluent uses independent dynamic resources for an open ComboBox popup and scrollbar
/// tracks/thumbs, so styling ComboBox.Background alone must not be considered sufficient.
/// </summary>
public class PopupScrollbarThemeTests
{
    [AvaloniaTheory]
    [InlineData(UiTheme.ThemeNameDark, true)]
    [InlineData(UiTheme.ThemeNameNord, true)]
    [InlineData(UiTheme.ThemeNameMidnightCinema, true)]
    [InlineData(UiTheme.ThemeNameLight, false)]
    [InlineData(UiTheme.ThemeNameClassic, false)]
    [InlineData(UiTheme.ThemeNamePastel, false)]
    [InlineData(UiTheme.ThemeNameSakura, false)]
    public void PopupAndScrollbarResources_FollowEditorPalette(string theme, bool dark)
    {
        var app = Application.Current!;
        var appearance = Se.Settings.Appearance;
        var oldTheme = appearance.Theme;
        var oldVariant = app.RequestedThemeVariant;
        try
        {
            appearance.Theme = theme;
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            EditorChrome.InvalidatePalette();
            UiTheme.ApplyPopupAndScrollColors();

            var palette = EditorChrome.Palette;
            Assert.Equal(palette.Menu, GetBrushColor("ComboBoxDropDownBackground"));
            Assert.Equal(palette.MenuBorder, GetBrushColor("ComboBoxDropDownBorderBrush"));
            Assert.Equal(palette.Panel, GetBrushColor("ScrollBarBackground"));
            Assert.Equal(palette.Panel, GetBrushColor("ScrollBarTrackFill"));
            Assert.Equal(palette.StrongText, GetBrushColor("ComboBoxItemForeground"));
            Assert.True(UiTheme.GetContrastRatio(
                GetBrushColor("ScrollBarThumbBackgroundColor"), palette.Panel) >= 1.5,
                "Scrollbar thumb must remain visibly different from its track.");
        }
        finally
        {
            appearance.Theme = oldTheme;
            app.RequestedThemeVariant = oldVariant;
            EditorChrome.InvalidatePalette();
            UiTheme.ApplyPopupAndScrollColors();
        }
    }

    [AvaloniaFact]
    public void PopupAndScrollbarResources_UpdateAfterSystemVariantChanges()
    {
        var app = Application.Current!;
        var appearance = Se.Settings.Appearance;
        var oldTheme = appearance.Theme;
        var oldVariant = app.RequestedThemeVariant;
        try
        {
            appearance.Theme = UiTheme.ThemeNameSystem;
            UiTheme.SetCurrentTheme();

            app.RequestedThemeVariant = ThemeVariant.Dark;
            var darkPopup = GetBrushColor("ComboBoxDropDownBackground");
            var darkThumb = GetBrushColor("ScrollBarThumbBackgroundColor");

            app.RequestedThemeVariant = ThemeVariant.Light;
            var lightPopup = GetBrushColor("ComboBoxDropDownBackground");
            var lightThumb = GetBrushColor("ScrollBarThumbBackgroundColor");

            Assert.NotEqual(darkPopup, lightPopup);
            Assert.NotEqual(darkThumb, lightThumb);
        }
        finally
        {
            appearance.Theme = oldTheme;
            app.RequestedThemeVariant = oldVariant;
            UiTheme.SetCurrentTheme();
        }
    }

    private static Color GetBrushColor(string key)
    {
        var app = Application.Current!;
        Assert.True(app.TryGetResource(key, app.ActualThemeVariant, out var value), key);
        return Assert.IsAssignableFrom<ISolidColorBrush>(value).Color;
    }
}
