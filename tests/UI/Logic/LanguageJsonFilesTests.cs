using System.Text.Json;

namespace UITests.Logic;

/// <summary>
/// Validates that every translation file shipped in <c>src/ui/Assets/Languages</c> is well-formed
/// JSON, so a broken language file is caught here rather than at application start-up.
/// </summary>
public class LanguageJsonFilesTests
{
    /// <summary>One theory case per <c>*.json</c> file in the Languages folder.</summary>
    public static TheoryData<string> LanguageFileNames()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.GetFiles(GetLanguagesFolder(), "*.json"))
        {
            data.Add(Path.GetFileName(path));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(LanguageFileNames))]
    public void LanguageFile_IsValidJson(string fileName)
    {
        var path = Path.Combine(GetLanguagesFolder(), fileName);

        // File.ReadAllText strips a UTF-8 BOM; the language files are saved with one.
        var json = File.ReadAllText(path);

        var exception = Record.Exception(() => JsonDocument.Parse(json).Dispose());

        Assert.True(exception is null, $"{fileName} is not valid JSON: {exception?.Message}");
    }

    [Fact]
    public void Portuguese_HasSameTranslationLeafKeySetAsEnglish()
    {
        var folder = GetLanguagesFolder();
        using var english = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "English.json")));
        using var portuguese = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "Portuguese.json")));

        var englishEntries = GetStringEntries(english.RootElement).ToDictionary(x => x.Path, x => x.Value, StringComparer.Ordinal);
        var portugueseEntries = GetStringEntries(portuguese.RootElement).ToDictionary(x => x.Path, x => x.Value, StringComparer.Ordinal);

        foreach (var metadataKey in new[] { "title", "version", "translatedBy", "cultureName" })
        {
            englishEntries.Remove(metadataKey);
            portugueseEntries.Remove(metadataKey);
        }

        var missing = englishEntries.Keys.Except(portugueseEntries.Keys).OrderBy(x => x).ToArray();
        var extra = portugueseEntries.Keys.Except(englishEntries.Keys).OrderBy(x => x).ToArray();
        var missingWithEnglish = missing.Select(x => $"{x} = {JsonSerializer.Serialize(englishEntries[x])}");

        Assert.True(missing.Length == 0 && extra.Length == 0,
            $"Portuguese.json translation drift. Missing leaf strings ({missing.Length}):\n" +
            string.Join("\n", missingWithEnglish) +
            $"\nExtra leaf strings ({extra.Length}):\n" + string.Join("\n", extra));
    }

    [Fact]
    public void LanguagesFolder_ContainsLanguageFiles()
    {
        var files = Directory.GetFiles(GetLanguagesFolder(), "*.json");

        Assert.NotEmpty(files);
        Assert.Contains(files, f => Path.GetFileName(f) == "English.json");
    }

    private static IEnumerable<(string Path, string Value)> GetStringEntries(JsonElement element, string prefix = "")
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (var property in element.EnumerateObject())
        {
            var path = string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}";
            if (property.Value.ValueKind == JsonValueKind.String)
            {
                yield return (path, property.Value.GetString() ?? string.Empty);
                continue;
            }

            foreach (var child in GetStringEntries(property.Value, path))
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// Walks up from the test output directory to the repository root and returns the
    /// <c>src/ui/Assets/Languages</c> folder. Throws when it cannot be found.
    /// </summary>
    private static string GetLanguagesFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "ui", "Assets", "Languages");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate 'src/ui/Assets/Languages' walking up from '{AppContext.BaseDirectory}'.");
    }
}
