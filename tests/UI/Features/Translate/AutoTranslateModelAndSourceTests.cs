using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Features.Translate;
using Nikse.SubtitleEdit.Logic;
using Nikse.SubtitleEdit.Logic.Config;
using Nikse.SubtitleEdit.Logic.Media;
using Nikse.SubtitleEdit.UiLogic.AutoTranslate;
using Nikse.SubtitleEdit.UiLogic.Translate;
using System.Collections.ObjectModel;

namespace UITests.Features.Translate;

/// <summary>
/// #14926 follow-ups in the auto-translate window: the engines' known models are offered in a
/// drop-down, and a source language that cannot be detected no longer defaults to English.
/// </summary>
public class AutoTranslateModelAndSourceTests
{
    private sealed class NullServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    [AvaloniaFact]
    public void EngineWithKnownModels_ShowsModelDropDown_ElseTextBox()
    {
        var lastName = Se.Settings.AutoTranslate.AutoTranslateLastName;
        var geminiModel = Se.Settings.AutoTranslate.GeminiModel;
        var vm = new AutoTranslateViewModel(new WindowService(new NullServiceProvider()), new FolderHelper());
        var subtitle = new Subtitle();
        subtitle.Paragraphs.Add(new Paragraph("Hello there.", 0, 2000));
        vm.Initialize(subtitle);
        var window = new AutoTranslateWindow(vm) { Width = 1000, Height = 700 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            vm.SelectedAutoTranslator = vm.AutoTranslators.First(t => t is GeminiTranslate);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.True(vm.ModelComboIsVisible);
            Assert.False(vm.ModelTextBoxIsVisible);
            Assert.Contains("gemini-flash-lite-latest", vm.ModelPresets);
            var combo = window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.IsEditable && c.IsVisible);
            Assert.False(string.IsNullOrEmpty(vm.ModelText));
            Assert.Equal(vm.ModelText, combo.Text);

            combo.Text = "gemini-flash-lite-latest";
            Assert.Equal("gemini-flash-lite-latest", vm.ModelText);

            // Swapping one model list for another must not wipe the model the new engine restored.
            vm.SelectedAutoTranslator = vm.AutoTranslators.First(t => t is AnthropicTranslate);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.ModelComboIsVisible);
            Assert.False(string.IsNullOrEmpty(vm.ModelText));
            Assert.Equal(vm.ModelText, combo.Text);

            vm.SelectedAutoTranslator = vm.AutoTranslators.First(t => t is GeminiTranslate);
            Dispatcher.UIThread.RunJobs();
            Assert.False(string.IsNullOrEmpty(vm.ModelText));
            Assert.Equal(vm.ModelText, combo.Text);

            vm.SelectedAutoTranslator = vm.AutoTranslators.First(t => t is OpenAiCompatibleTranslate);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.ModelTextBoxIsVisible);
            Assert.False(vm.ModelComboIsVisible);

            vm.SelectedAutoTranslator = vm.AutoTranslators.First(t => t is GoogleTranslateV1);
            Dispatcher.UIThread.RunJobs();
            Assert.False(vm.ModelTextBoxIsVisible);
            Assert.False(vm.ModelComboIsVisible);
        }
        finally
        {
            window.Close();
            Se.Settings.AutoTranslate.AutoTranslateLastName = lastName;
            Se.Settings.AutoTranslate.GeminiModel = geminiModel;
        }
    }

    [Fact]
    public void UndetectableSource_UsesLastSource_NotEnglish()
    {
        var lastSource = Se.Settings.AutoTranslate.AutoTranslateLastSource;
        try
        {
            var subtitle = new Subtitle();
            subtitle.Paragraphs.Add(new Paragraph("Ja, klar.", 0, 1000));
            var languages = new ObservableCollection<TranslationPair>(new GoogleTranslateV1().GetSupportedSourceLanguages());

            Se.Settings.AutoTranslate.AutoTranslateLastSource = "de";
            Assert.Equal("de", AutoTranslateViewModel.EvaluateDefaultSourceLanguageCode(null, subtitle, languages));

            Se.Settings.AutoTranslate.AutoTranslateLastSource = string.Empty;
            Assert.Equal("en", AutoTranslateViewModel.EvaluateDefaultSourceLanguageCode(null, subtitle, languages));
        }
        finally
        {
            Se.Settings.AutoTranslate.AutoTranslateLastSource = lastSource;
        }
    }

    [Fact]
    public void UndetectableSource_NllbSavedCodeMapsToCurrentProvider()
    {
        var lastSource = Se.Settings.AutoTranslate.AutoTranslateLastSource;
        try
        {
            var subtitle = new Subtitle();
            subtitle.Paragraphs.Add(new Paragraph("...", 0, 1000));
            var languages = new ObservableCollection<TranslationPair>(new GoogleTranslateV1().GetSupportedSourceLanguages());

            Se.Settings.AutoTranslate.AutoTranslateLastSource = "eng_Latn";

            Assert.Equal("en", AutoTranslateViewModel.EvaluateDefaultSourceLanguageCode(null, subtitle, languages));
        }
        finally
        {
            Se.Settings.AutoTranslate.AutoTranslateLastSource = lastSource;
        }
    }

    [Fact]
    public void LastNllbTarget_MapsToCurrentProviderLanguage()
    {
        var translator = new GeminiTranslate();
        var targets = translator.GetSupportedTargetLanguages();
        var source = translator.GetSupportedSourceLanguages().First(p => p.Name == "English");

        var selected = AutoTranslateViewModel.FindDefaultTargetLanguage(
            targets,
            source,
            lastTarget: "deu_Latn",
            uiCultureName: "en-US");

        Assert.NotNull(selected);
        Assert.Equal("German", selected.Name);
    }

    [Fact]
    public void ScriptQualifiedNllbTarget_PreservesVariantAcrossSeparatorConventions()
    {
        var translator = new GeminiTranslate();
        var targets = translator.GetSupportedTargetLanguages();
        var source = translator.GetSupportedSourceLanguages().First(p => p.Name == "English");

        var selected = AutoTranslateViewModel.FindDefaultTargetLanguage(
            targets,
            source,
            lastTarget: "zho_Hans",
            uiCultureName: "en-US");

        Assert.NotNull(selected);
        Assert.Equal("Chinese (Simplified)", selected.Name);
    }

    [Fact]
    public void ScriptQualifiedNllbTraditionalChinese_MapsToCurrentProvider()
    {
        var translator = new GeminiTranslate();
        var targets = translator.GetSupportedTargetLanguages();
        var source = translator.GetSupportedSourceLanguages().First(p => p.Name == "English");

        var selected = AutoTranslateViewModel.FindDefaultTargetLanguage(
            targets,
            source,
            lastTarget: "zho_Hant",
            uiCultureName: "en-US");

        Assert.NotNull(selected);
        Assert.Equal("Chinese (Traditional)", selected.Name);
    }

    [Fact]
    public void ScriptQualifiedNllbSimplifiedChinese_MapsToGoogleStyleProvider()
    {
        var translator = new GoogleTranslateV1();
        var targets = translator.GetSupportedTargetLanguages();
        var source = translator.GetSupportedSourceLanguages().First(p => p.Code == "en");

        var selected = AutoTranslateViewModel.FindDefaultTargetLanguage(
            targets,
            source,
            lastTarget: "zho_Hans",
            uiCultureName: "en-US");

        Assert.NotNull(selected);
        Assert.Equal("zh-CN", selected.Code);
    }

    [Fact]
    public void UnknownChineseScript_DoesNotGuessTheWrongVariant()
    {
        var translator = new GeminiTranslate();
        var targets = translator.GetSupportedTargetLanguages();
        var source = translator.GetSupportedSourceLanguages().First(p => p.Name == "English");

        var selected = AutoTranslateViewModel.FindDefaultTargetLanguage(
            targets,
            source,
            lastTarget: "zho_UnknownScript",
            uiCultureName: "fr-FR");

        Assert.NotNull(selected);
        Assert.Equal("French", selected.Name);
    }

    [Fact]
    public void RememberLanguageCodes_PreservesNllbScriptAfterTranslation()
    {
        var previousSource = Se.Settings.AutoTranslate.AutoTranslateLastSource;
        var previousTarget = Se.Settings.AutoTranslate.AutoTranslateLastTarget;
        try
        {
            var engine = new NoLanguageLeftBehindServe();
            var languages = engine.GetSupportedTargetLanguages();
            var source = languages.Single(p => p.Code == "eng_Latn");
            var target = languages.Single(p => p.Code == "zho_Hant");

            Assert.Equal("zh", target.TwoLetterIsoLanguageName);
            AutoTranslateViewModel.RememberLanguageCodes(source, target);
            Assert.Equal("eng_Latn", Se.Settings.AutoTranslate.AutoTranslateLastSource);
            Assert.Equal("zho_Hant", Se.Settings.AutoTranslate.AutoTranslateLastTarget);
        }
        finally
        {
            Se.Settings.AutoTranslate.AutoTranslateLastSource = previousSource;
            Se.Settings.AutoTranslate.AutoTranslateLastTarget = previousTarget;
        }
    }

    [Fact]
    public void OldAmbiguousChineseIso_DoesNotGuessNllbSimplified()
    {
        var nllb = new NoLanguageLeftBehindServe();
        var languages = nllb.GetSupportedTargetLanguages();
        var english = languages.Single(p => p.Code == "eng_Latn");
        var result = AutoTranslateViewModel.FindDefaultTargetLanguage(
            languages, english, lastTarget: "zh", uiCultureName: "en-US");

        Assert.NotNull(result);
        Assert.NotEqual("zho_Hans", result.Code);
        Assert.NotEqual("zho_Hant", result.Code);
    }

    [Theory]
    [InlineData("zh-TW", "Chinese (Traditional)")]
    [InlineData("zh-CN", "Chinese (Simplified)")]
    [InlineData("pt-PT", "Portuguese")]
    [InlineData("pt-BR", "Brazilian Portuguese")]
    [InlineData("arb_Arab", "Arabic")]
    [InlineData("pes_Arab", "Persian")]
    [InlineData("zsm_Latn", "Malay")]
    [InlineData("yue_Hant", "Cantonese")]
    public void CrossProviderIdentifiers_MapToGemini(string previousCode, string expected)
    {
        var engine = new GeminiTranslate();
        var targets = engine.GetSupportedTargetLanguages();
        var english = targets.Single(p => p.Name == "English");
        var selected = AutoTranslateViewModel.FindDefaultTargetLanguage(
            targets, english, previousCode, uiCultureName: "en-US");
        Assert.NotNull(selected);
        Assert.Equal(expected, selected.Name);
    }

    [Theory]
    [InlineData("zh-TW", "zho_Hant")]
    [InlineData("zh-CN", "zho_Hans")]
    [InlineData("pt-PT", "por_Latn")]
    public void GoogleVariants_MapToNllb(string previousCode, string expected)
    {
        var engine = new NoLanguageLeftBehindServe();
        var targets = engine.GetSupportedTargetLanguages();
        var english = targets.Single(p => p.Code == "eng_Latn");
        var selected = AutoTranslateViewModel.FindDefaultTargetLanguage(
            targets, english, previousCode, uiCultureName: "en-US");
        Assert.NotNull(selected);
        Assert.Equal(expected, selected.Code);
    }

    [Fact]
    public void LegacyGeminiPortugueseIso_MapsToEuropeanGoogle_NotBrazilian()
    {
        var google = new GoogleTranslateV1();
        var targets = google.GetSupportedTargetLanguages();
        var english = targets.Single(p => p.Code == "en");
        var selected = AutoTranslateViewModel.FindDefaultTargetLanguage(
            targets, english, lastTarget: "pt", uiCultureName: "en-US",
            savedProviderName: GeminiTranslate.StaticName);
        Assert.NotNull(selected);
        Assert.Equal("pt-PT", selected.Code);
    }

    [Fact]
    public void LegacyGoogleBrazilianIso_RemainsBrazilian()
    {
        var google = new GoogleTranslateV1();
        var targets = google.GetSupportedTargetLanguages();
        var english = targets.Single(p => p.Code == "en");
        var selected = AutoTranslateViewModel.FindDefaultTargetLanguage(
            targets, english, lastTarget: "pt", uiCultureName: "en-US",
            savedProviderName: GoogleTranslateV1.StaticName);
        Assert.NotNull(selected);
        Assert.Equal("pt", selected.Code);
    }

    [Fact]
    public void SwitchTranslator_NllbChineseTraditional_StaysTraditional()
    {
        var nllb = new NoLanguageLeftBehindServe();
        var previous = nllb.GetSupportedTargetLanguages().Single(p => p.Code == "zho_Hant");
        var gemini = new GeminiTranslate();
        var next = AutoTranslateViewModel.FindSameLanguage(previous, gemini.GetSupportedTargetLanguages());
        Assert.NotNull(next);
        Assert.Equal("Chinese (Traditional)", next.Name);
    }

    [Theory]
    [InlineData("pt", "PORTUGUESE (BRAZIL)", "Brazilian Portuguese")]
    [InlineData("pt-PT", "PORTUGUESE", "Portuguese")]
    public void SwitchTranslator_GooglePortugueseVariants_DoNotCross(string code, string name, string expected)
    {
        var google = new GoogleTranslateV1();
        var previous = google.GetSupportedTargetLanguages().Single(p => p.Code == code);
        Assert.Equal(name, previous.Name);
        var gemini = new GeminiTranslate();
        var next = AutoTranslateViewModel.FindSameLanguage(previous, gemini.GetSupportedTargetLanguages());
        Assert.NotNull(next);
        Assert.Equal(expected, next.Name);
    }

    [Theory]
    [InlineData("arb_Latn")] // Romanized Arabic is not the Arabic-script Google option.
    [InlineData("ace_Arab")] // Acehnese has distinct NLLB Arabic/Latin options.
    [InlineData("bjn_Arab")]
    [InlineData("min_Arab")]
    [InlineData("kas_Deva")]
    [InlineData("taq_Tfng")]
    [InlineData("eng_Cyrl")] // Not a supported NLLB code: do not invent an English variant.
    public void UnrepresentedScript_IsNotSilentlyMappedToGenericProvider(string previousCode)
    {
        var targets = new GoogleTranslateV1().GetSupportedTargetLanguages();
        Assert.Null(AutoTranslateViewModel.FindLanguage(targets, previousCode));
    }

    [Fact]
    public void NativeScriptArabic_CanStillMapToGenericArabic()
    {
        var targets = new GoogleTranslateV1().GetSupportedTargetLanguages();
        var match = AutoTranslateViewModel.FindLanguage(targets, "arb_Arab");
        Assert.NotNull(match);
        Assert.Equal("ar", match.Code);
    }

    [Fact]
    public void SwitchProvider_DoesNotTurnRomanizedArabicIntoArabicScript()
    {
        var nllb = new NoLanguageLeftBehindServe();
        var previous = nllb.GetSupportedTargetLanguages().Single(p => p.Code == "arb_Latn");
        var google = new GoogleTranslateV1();
        Assert.Null(AutoTranslateViewModel.FindSameLanguage(previous, google.GetSupportedTargetLanguages()));
    }

    [Fact]
    public void UnsupportedScript_UsesSafeCultureFallbackRatherThanWrongTarget()
    {
        var google = new GoogleTranslateV1();
        var targets = google.GetSupportedTargetLanguages();
        var english = targets.Single(p => p.Code == "en");
        var chosen = AutoTranslateViewModel.FindDefaultTargetLanguage(
            targets, english, lastTarget: "arb_Latn", uiCultureName: "fr-FR");
        Assert.NotNull(chosen);
        Assert.Equal("fr", chosen.Code);
    }

    [Fact]
    public void DetectableSource_IsStillDetected()
    {
        var lastSource = Se.Settings.AutoTranslate.AutoTranslateLastSource;
        try
        {
            var subtitle = new Subtitle();
            subtitle.Paragraphs.Add(new Paragraph("¿Dónde está mi coche?", 0, 1000));
            var languages = new ObservableCollection<TranslationPair>(new GoogleTranslateV1().GetSupportedSourceLanguages());

            Se.Settings.AutoTranslate.AutoTranslateLastSource = "de";
            Assert.Equal("es", AutoTranslateViewModel.EvaluateDefaultSourceLanguageCode(null, subtitle, languages));
        }
        finally
        {
            Se.Settings.AutoTranslate.AutoTranslateLastSource = lastSource;
        }
    }
}
