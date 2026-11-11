using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Nikse.SubtitleEdit;
using Nikse.SubtitleEdit.Features.Main;
using Nikse.SubtitleEdit.Features.Main.Layout;
using Nikse.SubtitleEdit.Logic.Config;

namespace UITests.Features.Main;

/// <summary>
/// The edit-section splitter must stop before the lowest visible text action button.
/// The text box and time controls alone do not cover a tall optional button column.
/// </summary>
public class EditSectionButtonPanelHeightTests
{
    [AvaloniaTheory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(7)]
    public void EditSectionRow_KeepsAllEnabledTextButtonsVisibleAtMinimumHeight(int buttonCount)
    {
        var appearance = Se.Settings.Appearance;
        var previous = new[]
        {
            appearance.TextBoxShowButtonAutoBreak,
            appearance.TextBoxShowButtonUnbreak,
            appearance.TextBoxShowButtonItalic,
            appearance.TextBoxShowButtonColor,
            appearance.TextBoxShowButtonRemoveFormatting,
            appearance.TextBoxShowButtonAiAssistant,
            appearance.TextBoxShowButtonRecordSpeech,
        };

        appearance.TextBoxShowButtonAutoBreak = buttonCount >= 1;
        appearance.TextBoxShowButtonUnbreak = buttonCount >= 2;
        appearance.TextBoxShowButtonItalic = buttonCount >= 3;
        appearance.TextBoxShowButtonColor = buttonCount >= 4;
        appearance.TextBoxShowButtonRemoveFormatting = buttonCount >= 5;
        appearance.TextBoxShowButtonAiAssistant = buttonCount >= 6;
        appearance.TextBoxShowButtonRecordSpeech = buttonCount >= 7;

        var services = new ServiceCollection();
        services.AddSubtitleEditServices();
        Locator.Services = services.BuildServiceProvider();

        var window = new Window { Width = 1200, Height = 800 };
        MainView? view = null;
        try
        {
            MainView.NextHostWindow = window;
            view = new MainView();
            window.Content = view;
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var vm = (MainViewModel)view.DataContext!;
            var textEditGrid = Assert.Single(window.GetLogicalDescendants().OfType<Grid>(),
                g => g.Name == "SubtitleTextEditGrid");
            var editGrid = Assert.IsType<Grid>(textEditGrid.GetLogicalParent());
            var buttonPanel = Assert.Single(textEditGrid.Children.OfType<StackPanel>(),
                p => p.Name == InitListViewAndEditBox.EditBoxButtonPanelName);
            Assert.Equal(buttonCount, buttonPanel.Children.Count);

            var row = Assert.IsType<RowDefinition>(vm.EditSectionRow);
            row.Height = new GridLength(row.MinHeight, GridUnitType.Pixel);
            Dispatcher.UIThread.RunJobs();

            var lastButton = buttonPanel.Children.Last();
            var bottom = textEditGrid.Bounds.Top + buttonPanel.Bounds.Top + lastButton.Bounds.Bottom
                         + buttonPanel.Margin.Bottom;
            var availableBottom = editGrid.Bounds.Height - editGrid.Margin.Bottom;

            Assert.True(bottom <= availableBottom + 0.5,
                $"Last action button bottom {bottom:0.0}px exceeds edit section {availableBottom:0.0}px at minimum height ({buttonCount} buttons).");
        }
        finally
        {
            if (view?.DataContext is MainViewModel vm)
            {
                foreach (var ownedWindow in window.OwnedWindows.ToArray())
                {
                    ownedWindow.Close();
                }

                window.SuppressSaveChangesPromptOnClose(vm);
            }

            if (window.IsVisible)
            {
                window.Close();
            }

            appearance.TextBoxShowButtonAutoBreak = previous[0];
            appearance.TextBoxShowButtonUnbreak = previous[1];
            appearance.TextBoxShowButtonItalic = previous[2];
            appearance.TextBoxShowButtonColor = previous[3];
            appearance.TextBoxShowButtonRemoveFormatting = previous[4];
            appearance.TextBoxShowButtonAiAssistant = previous[5];
            appearance.TextBoxShowButtonRecordSpeech = previous[6];
        }
    }
}
