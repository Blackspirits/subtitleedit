using System.Text.Json;
using Nikse.SubtitleEdit.Core.Settings;
using Nikse.SubtitleEdit.UiLogic.AutoTranslate;

namespace LibUiLogicTests.AutoTranslate;

public class ApiRouteTranslateTests
{
    [Fact]
    public void Models_FirstSuggestionMatchesPersistentDefault()
    {
        var settings = new ToolsSettings();

        Assert.NotEmpty(ApiRouteTranslate.Models);
        Assert.Equal(ApiRouteTranslate.Models[0], settings.ApiRouteModel);
        Assert.Equal("gpt-5.6-sol", settings.ApiRouteModel);
    }

    [Fact]
    public void BuildRequestBody_SerializesModelAndMessageWithoutJsonInjection()
    {
        const string model = "custom\\\"model\\name";
        const string prompt = "Translate from English to Portuguese.";
        const string text = "First line\nSecond \"quoted\" line";

        var body = ApiRouteTranslate.BuildRequestBody(model, prompt, text);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(model, root.GetProperty("model").GetString());

        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal(prompt + "\n\n" + text, message.GetProperty("content").GetString());
        Assert.Equal(2, root.EnumerateObject().Count());
    }
}
