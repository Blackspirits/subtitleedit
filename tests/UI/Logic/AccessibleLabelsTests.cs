using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Nikse.SubtitleEdit.Logic;
using Xunit;

namespace UITests.Logic;

public class AccessibleLabelsTests
{
    [AvaloniaFact]
    public void Apply_DoesNotUseEmptyTextBlockAsLabel()
    {
        var grid = new Grid();
        var label = new TextBlock();
        var input = new TextBox();

        Grid.SetColumn(input, 1);
        grid.Children.Add(label);
        grid.Children.Add(input);

        AccessibleLabels.Apply(grid);

        Assert.Null(AutomationProperties.GetLabeledBy(input));
        Assert.False(AccessibleLabels.HasAccessibleName(input));
    }

    [AvaloniaFact]
    public void Apply_DoesNotUseEmptyLabelAsLabel()
    {
        var grid = new Grid();
        var label = new Label();
        var input = new TextBox();

        Grid.SetColumn(input, 1);
        grid.Children.Add(label);
        grid.Children.Add(input);

        AccessibleLabels.Apply(grid);

        Assert.Null(AutomationProperties.GetLabeledBy(input));
        Assert.False(AccessibleLabels.HasAccessibleName(input));
    }

    [AvaloniaFact]
    public void Apply_StillUsesVisibleShortTextAsLabel()
    {
        var grid = new Grid();
        var label = new TextBlock { Text = "Frame rate" };
        var input = new ComboBox();

        Grid.SetColumn(input, 1);
        grid.Children.Add(label);
        grid.Children.Add(input);

        AccessibleLabels.Apply(grid);

        Assert.Same(label, AutomationProperties.GetLabeledBy(input));
        Assert.True(AccessibleLabels.HasAccessibleName(input));
    }
}
