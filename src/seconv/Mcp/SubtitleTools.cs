using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nikse.SubtitleEdit.UiLogic.Export;
using SeConv.Core;

namespace SeConv.Mcp;

/// <summary>
/// The MCP tool surface of <c>seconv mcp</c>. Each tool is a thin adapter over the same Core
/// helpers the CLI subcommands use (<see cref="SubtitleInfoGatherer"/>, <see cref="SubtitleLinter"/>,
/// <see cref="SubtitleConverter"/>, ...). The MCP conversion tool intentionally exposes a focused
/// subset of the CLI's current options; controls it does expose use the same core semantics.
/// Every tool returns a <see cref="CallToolResult"/> directly: on success a single compact JSON
/// text block; validation/business errors are actionable, while unexpected internal exceptions are
/// logged to stderr and returned as a generic tool error so local paths/details are not leaked.
/// </summary>
[McpServerToolType]
internal sealed class SubtitleTools
{
    private SubtitleTools()
    {
        // Static tool methods only; the SDK's WithTools<T>() needs a non-static type argument.
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Installed once on server startup; never changed during an MCP session.
    // Direct in-process unit tests call the methods without starting the server.
    private static McpPathPolicy? _pathPolicy;

    internal static void ConfigurePathPolicy(McpPathPolicy policy)
    {
        _pathPolicy = policy;
    }

    // Keep single-file image outputs and VobSub (.sub/.idx: both are validated).
    // All other image handlers are denied by default, including future additions.
    internal static bool IsUnsupportedMultiFileImageTarget(string format)
    {
        var handler = ImageOutputWriter.TryCreateHandler(LibSEIntegration.NormalizeFormatName(format));
        return handler is not null && handler is not (
            ExportHandlerBluRaySup or
            ExportHandlerDvdSup or
            ExportHandlerVobSub or
            ExportHandlerDCinemaSmpte2014Mxf);
    }

    private const int DefaultReadCount = 100;
    private const int MaxReadCount = 1000;

    [McpServerTool(Name = "list_formats", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List subtitle engine formats. 'inputOnly' means not writable; 'mcpOutputSupported' also excludes image targets that generate unchecked sidecar files.")]
    public static CallToolResult ListFormats(
        [Description("Optional case-insensitive substring matched against id, name and extension (e.g. 'srt', 'ebu', 'vtt'). Omit to list everything.")] string? filter = null,
        CancellationToken cancellationToken = default)
        => Run(() =>
        {
            var formats = LibSEIntegration.GetAvailableFormats()
                .Select(entry => new
                {
                    id = entry.Format.Name.Replace(" ", string.Empty),
                    name = entry.Format.Name,
                    extension = entry.Format.Extension,
                    type = entry.Kind.StartsWith("binary", StringComparison.Ordinal) ? "binary" : "text",
                    inputOnly = entry.Kind.Contains("(input)", StringComparison.Ordinal),
                    mcpOutputSupported = !entry.Kind.Contains("(input)", StringComparison.Ordinal) &&
                                         !IsUnsupportedMultiFileImageTarget(entry.Format.Name),
                })
                .Where(f => string.IsNullOrWhiteSpace(filter) ||
                            f.id.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            f.name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                            f.extension.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return new
            {
                total = formats.Count,
                formats,
                extraIds = new[] { "plaintext", "bluraysup", "vobsub" },
                note = "MCP disallows image outputs with unchecked PNG/XML companions; use the CLI for those formats.",
            };
        }, cancellationToken);

    [McpServerTool(Name = "subtitle_info", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Detect a subtitle file's format, encoding, paragraph count, first/last time codes, duration and language. Works across supported text and binary subtitle formats.")]
    public static CallToolResult SubtitleInfo(
        [Description("Path to the subtitle file.")] string path,
        CancellationToken cancellationToken = default)
        => Run(() =>
        {
            _pathPolicy?.RequireReadWithCompanions(path);
            return SubtitleInfoGatherer.Gather(path);
        }, cancellationToken);

    [McpServerTool(Name = "read_subtitle", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read the paragraphs (number, start/end time, text) of a subtitle file in any supported text or binary format, paged. Use this instead of reading the raw file when the format is not plain SRT/VTT.")]
    public static CallToolResult ReadSubtitle(
        [Description("Path to the subtitle file.")] string path,
        [Description("1-based number of the first paragraph to return (default 1).")] int start = 1,
        [Description("Maximum number of paragraphs to return (default 100, max 1000).")] int count = DefaultReadCount,
        [Description("Input text encoding (e.g. 'windows-1252'). Default: auto-detect.")] string? encoding = null,
        CancellationToken cancellationToken = default)
        => Run(() =>
        {
            _pathPolicy?.RequireReadWithCompanions(path);
            var (subtitle, format) = LibSEIntegration.LoadSubtitleWithFormat(path, encoding);
            var total = subtitle.Paragraphs.Count;
            var first = Math.Max(1, start);
            var take = Math.Clamp(count, 1, MaxReadCount);

            var paragraphs = subtitle.Paragraphs
                .Skip(first - 1)
                .Take(take)
                .Select((p, i) => new
                {
                    number = first + i,
                    start = p.StartTime.ToDisplayString(),
                    end = p.EndTime.ToDisplayString(),
                    startMs = (long)p.StartTime.TotalMilliseconds,
                    endMs = (long)p.EndTime.TotalMilliseconds,
                    text = p.Text,
                })
                .ToList();

            return new
            {
                path,
                format = format.Name,
                total,
                start = first,
                returned = paragraphs.Count,
                hasMore = first - 1 + paragraphs.Count < total,
                paragraphs,
            };
        }, cancellationToken);

    [McpServerTool(Name = "lint_subtitle", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Validate a subtitle file without modifying it: overlapping or too short/long display times, lines that are too long, too many lines, empty paragraphs, mismatched italic/bold tags. Returns the issues per paragraph number.")]
    public static CallToolResult LintSubtitle(
        [Description("Path to the subtitle file.")] string path,
        CancellationToken cancellationToken = default)
        => Run(() =>
        {
            _pathPolicy?.RequireReadWithCompanions(path);
            return SubtitleLinter.Lint(path);
        }, cancellationToken);

    [McpServerTool(Name = "list_fix_common_errors_rules", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the FixCommonErrors rule ids accepted by convert_subtitle's 'fixCommonErrorsRules' parameter, with the matching Subtitle Edit GUI label and the language gate (if any).")]
    public static CallToolResult ListFixCommonErrorsRules(CancellationToken cancellationToken = default)
        => Run(() => new
        {
            total = FixCommonErrorsRunner.AvailableRuleIds.Count,
            rules = FixCommonErrorsRunner.AvailableRuleIds.Select(id => new
            {
                id,
                guiLabel = FixCommonErrorsRunner.GuiLabels.TryGetValue(id, out var label) ? label : null,
                languageGate = FixCommonErrorsRunner.LanguageGates.TryGetValue(id, out var lang) ? lang : null,
            }),
            syntax = new { all = "all", subset = "FixCommas,FixEllipsesStart", allExcept = "all,-FixDanishLetterI" },
            note = "A language-gated rule runs only when the subtitle's language matches (auto-detected, or forced with fixCommonErrorsLanguage). Naming a gated rule selects it but does not bypass the gate.",
        }, cancellationToken);

    [McpServerTool(Name = "list_remove_formatting_rules", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("List the RemoveFormatting rule ids accepted by convert_subtitle's 'removeFormattingRules' parameter, with the matching Subtitle Edit GUI label.")]
    public static CallToolResult ListRemoveFormattingRules(CancellationToken cancellationToken = default)
        => Run(() => new
        {
            total = RemoveFormattingRunner.AvailableRuleIds.Count,
            rules = RemoveFormattingRunner.AvailableRuleIds.Select(id => new
            {
                id,
                guiLabel = RemoveFormattingRunner.GuiLabels.TryGetValue(id, out var label) ? label : null,
            }),
            syntax = new { all = "all", subset = "RemoveItalic,RemoveColor", allExcept = "all,-RemoveItalic" },
        }, cancellationToken);

    [McpServerTool(Name = "convert_subtitle", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Convert subtitles using a focused subset of seconv CLI options, including timing, OCR and cleanup. Image formats generating unvalidated companion files are not supported by MCP. Writes next to inputs unless outputFolder is provided; existing files are never overwritten unless overwrite is true. Failed or cancelled batches can leave earlier outputs. Use the CLI for advanced options.")]
    public static Task<CallToolResult> ConvertSubtitle(
        [Description("Input file paths or glob patterns (e.g. 'C:/subs/*.srt'). Containers (.mkv/.mp4/.ts/.avi) and image-based subtitles (.sup, .sub+.idx) are accepted too.")] string[] inputs,
        [Description("Target format id from list_formats (e.g. 'SubRip', 'WebVTT', 'AdvancedSubStationAlpha'); short aliases such as 'srt', 'vtt', 'ass', 'ebu' and 'plaintext' also work.")] string format,
        [Description("Folder to write the output files to. Default: next to each input file.")] string? outputFolder = null,
        [Description("Explicit output file name. Only valid with a single input file.")] string? outputFilename = null,
        [Description("Output text encoding: 'utf-8' (default, with BOM), 'utf-8-nobom', a code page name such as 'windows-1252', or 'source' to keep the input file's encoding.")] string? encoding = null,
        [Description("Overwrite an existing output file. Default false: a numeric suffix is added instead.")] bool overwrite = false,
        [Description("Shift every time code by this offset, e.g. '00:00:02.500', '2.5', '-1.2' (seconds) or '-00:00:01,000'.")] string? offset = null,
        [Description("Source frame rate for frame-based formats (e.g. 25).")] double? fps = null,
        [Description("Convert timings from 'fps' to this target frame rate (e.g. 23.976).")] double? targetFps = null,
        [Description("Renumber paragraphs starting at this value.")] int? renumber = null,
        [Description("Add this many milliseconds to every paragraph's duration (negative shortens).")] int? adjustDurationMs = null,
        [Description("Speed change in percent: 125 = 1.25x faster, 80 = slower.")] double? changeSpeedPercent = null,
        [Description("Bridge gaps shorter than this many milliseconds by extending the previous paragraph.")] int? bridgeGapsMaxMs = null,
        [Description("Enforce a minimum gap of this many milliseconds between consecutive paragraphs.")] int? applyMinGapMs = null,
        [Description("Delete the first N paragraphs.")] int? deleteFirst = null,
        [Description("Delete the last N paragraphs.")] int? deleteLast = null,
        [Description("Delete every paragraph whose text contains this string.")] string? deleteContains = null,
        [Description("Operations to apply, in this order. Valid names: FixCommonErrors, RemoveFormatting, RemoveTextForHI, BalanceLines, SplitLongLines, MergeShortLines, MergeSameTexts, MergeSameTimeCodes, RedoCasing, ApplyDurationLimits, BeautifyTimeCodes, ConvertColorsToDialog, FixRtlViaUnicodeChars, ReverseRtlStartEnd, RemoveLineBreaks, RemoveUnicodeControlChars.")] string[]? operations = null,
        [Description("FixCommonErrors rule selection: comma-separated ids from list_fix_common_errors_rules, 'all', or 'all,-RuleId'. Implies the FixCommonErrors operation.")] string? fixCommonErrorsRules = null,
        [Description("Force the language used by FixCommonErrors' language-gated rules (two-letter code such as 'en' or 'es'). Default: auto-detect from the text.")] string? fixCommonErrorsLanguage = null,
        [Description("RemoveFormatting rule selection: comma-separated ids from list_remove_formatting_rules, or 'all,-RuleId'. Implies the RemoveFormatting operation.")] string? removeFormattingRules = null,
        [Description("Container inputs: subtitle track numbers to extract. Default: every text track.")] int[]? trackNumbers = null,
        [Description("Image-based inputs: keep only the time codes and skip OCR (text is left empty).")] bool timeCodesOnly = false,
        [Description("OCR engine for image-based inputs: tesseract (default), nocr, binaryocr, ollama, paddle or llamacpp.")] string? ocrEngine = null,
        [Description("OCR language for image-based inputs (Tesseract ISO 639-2 code such as 'eng').")] string? ocrLanguage = null,
        [Description("Output resolution for image-based targets, e.g. '1920x1080'.")] string? resolution = null,
        CancellationToken cancellationToken = default)
        => RunAsync(async () =>
        {
            // The CLI trims entries before resolving paths, but decides whether to
            // split comma-delimited legacy inputs using the original string. Normalize
            // once and use the *same* values for authorization and conversion; a
            // leading space before an absolute comma-named path must not turn it into
            // multiple paths (including an unapproved second absolute path).
            var normalizedInputs = inputs?.Where(i => !string.IsNullOrWhiteSpace(i))
                .Select(i => i.Trim()).ToArray() ?? [];
            if (normalizedInputs.Length == 0)
            {
                throw new McpException("At least one input path or pattern is required.");
            }

            if (string.IsNullOrWhiteSpace(format))
            {
                throw new McpException("A target format is required. Use list_formats to see the ids.");
            }

            // Validate every input and all possible output directories before any
            // loader, converter or write is invoked. Access denied by default.
            _pathPolicy?.RequireConversionPaths(normalizedInputs, outputFolder, outputFilename);

            // Several image exporters create PNG/XML companions or output folders,
            // not just the validated destination. Until individual output artifacts
            // can be preflighted, do not expose those multi-file writers through MCP.
            // This restriction does not apply to the ordinary seconv CLI.
            if (IsUnsupportedMultiFileImageTarget(format))
            {
                throw new McpException(
                    "This image format creates additional output files that the MCP path policy " +
                    "cannot individually validate. Use the seconv CLI in a restricted directory.");
            }

            // The VobSub writer derives its .idx companion by replacing the
            // last three filename characters. An arbitrary explicit extension
            // could make that secondary write bypass the validated .sub/.idx pair.
            if (LibSEIntegration.NormalizeFormatName(format).Equals("VobSub", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(outputFilename) &&
                !outputFilename.EndsWith(".sub", StringComparison.OrdinalIgnoreCase))
            {
                throw new McpException("VobSub outputFilename must end with .sub so the companion .idx path can be validated.");
            }

            var normalizedRequestedFormat = format.Replace(" ", string.Empty);
            if (normalizedRequestedFormat.Equals("customtext", StringComparison.OrdinalIgnoreCase) ||
                normalizedRequestedFormat.Equals("customtextformat", StringComparison.OrdinalIgnoreCase))
            {
                throw new McpException(
                    "Custom text output requires a template and is not exposed by convert_subtitle. " +
                    "Use the seconv CLI with --format customtext --custom-format:<path.xml>.");
            }

            var ops = NormalizeOperations(operations);

            if (changeSpeedPercent.HasValue && changeSpeedPercent.Value <= 0)
            {
                throw new McpException($"changeSpeedPercent must be greater than 0 (got {changeSpeedPercent.Value}).");
            }

            IReadOnlyList<string> fceRules = [];
            if (!string.IsNullOrWhiteSpace(fixCommonErrorsRules))
            {
                fceRules = ParseRuleIds(
                    () => FixCommonErrorsRunner.ResolveRuleIds(fixCommonErrorsRules),
                    "seconv list-fce-rules",
                    "list_fix_common_errors_rules");
                EnsureOperation(ops, "FixCommonErrors");
            }

            IReadOnlyList<string>? rfRules = null;
            if (!string.IsNullOrWhiteSpace(removeFormattingRules))
            {
                rfRules = ParseRuleIds(
                    () => RemoveFormattingRunner.ResolveRuleIds(removeFormattingRules),
                    "seconv list-rf-rules",
                    "list_remove_formatting_rules");
                EnsureOperation(ops, "RemoveFormatting");
            }

            if (!string.IsNullOrWhiteSpace(fixCommonErrorsLanguage) &&
                FixCommonErrorsRunner.NormalizeLanguageOverride(fixCommonErrorsLanguage) is null)
            {
                throw new McpException(
                    $"fixCommonErrorsLanguage '{fixCommonErrorsLanguage}' is not recognized. " +
                    "Use a supported language code or English language name, or omit it for auto-detection.");
            }

            var options = new ConversionOptions
            {
                Patterns = normalizedInputs,
                Format = format,
                OutputFolder = outputFolder,
                OutputFilename = outputFilename,
                ValidateOutputPath = _pathPolicy is null ? null : _pathPolicy.RequireWrite,
                DenyReferencedImageInputs = true,
                Encoding = encoding,
                Overwrite = overwrite,
                Offset = string.IsNullOrWhiteSpace(offset) ? null : ParseClientInput(() => OffsetParser.Parse(offset)),
                Fps = fps,
                TargetFps = targetFps,
                Renumber = renumber,
                AdjustDurationMs = adjustDurationMs,
                ChangeSpeedPercent = changeSpeedPercent,
                BridgeGapsMaxMs = bridgeGapsMaxMs,
                ApplyMinGapMs = applyMinGapMs,
                DeleteFirst = deleteFirst,
                DeleteLast = deleteLast,
                DeleteContains = deleteContains,
                Operations = ops,
                FixCommonErrorsRules = fceRules,
                FixCommonErrorsLanguage = fixCommonErrorsLanguage,
                RemoveFormattingRules = rfRules,
                TrackNumbers = trackNumbers ?? [],
                TimeCodesOnly = timeCodesOnly,
                OcrEngine = string.IsNullOrWhiteSpace(ocrEngine) ? "tesseract" : ocrEngine,
                OcrLanguage = string.IsNullOrWhiteSpace(ocrLanguage) ? "eng" : ocrLanguage,
                Resolution = string.IsNullOrWhiteSpace(resolution) ? null : ParseClientInput(() => ResolutionParser.Parse(resolution)),
                // The converter narrates progress on stdout when not quiet; stdout is the MCP channel.
                Quiet = true,
            };

            var conversion = await new SubtitleConverter().ConvertAsync(options, cancellationToken);
            return JsonResult(conversion, isError: !conversion.Success);
        }, cancellationToken);

    /// <summary>
    /// Maps caller-supplied operation names onto the canonical names in
    /// <see cref="OperationOrderParser.ToggleOperations"/> (case-insensitive, dashes and
    /// underscores ignored, so "fix-common-errors" and "fixcommonerrors" both work). An unknown
    /// name is an error rather than a silent no-op, matching the CLI's strict option parsing.
    /// </summary>
    private static List<string> NormalizeOperations(string[]? operations)
    {
        var result = new List<string>();
        if (operations is null)
        {
            return result;
        }

        foreach (var raw in operations)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var key = raw.Replace("-", string.Empty).Replace("_", string.Empty);
            var canonical = OperationOrderParser.ToggleOperations
                .FirstOrDefault(op => op.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                throw new McpException(
                    $"Unknown operation '{raw}'. Valid operations: {string.Join(", ", OperationOrderParser.ToggleOperations)}.");
            }

            result.Add(canonical);
        }

        return result;
    }

    private static void EnsureOperation(List<string> operations, string name)
    {
        if (!operations.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            operations.Add(name);
        }
    }

    private static T ParseClientInput<T>(Func<T> parser)
    {
        try
        {
            return parser();
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (FormatException ex)
        {
            throw new McpException(ex.Message);
        }
    }

    private static T ParseRuleIds<T>(Func<T> parser, string cliCommand, string mcpTool)
    {
        try
        {
            return parser();
        }
        catch (ArgumentException ex)
        {
            var message = ex.Message.Replace(
                $"Run '{cliCommand}' to see available IDs.",
                $"Use {mcpTool} to see available IDs.",
                StringComparison.Ordinal);
            throw new McpException(message);
        }
    }

    // libse/seconv still uses process-wide settings in several read/convert paths. Keep the MCP
    // surface serialized until those settings are made request-scoped; the converter itself also
    // has a defensive gate for non-MCP callers.
    private static readonly SemaphoreSlim ToolGate = new(1, 1);

    private static CallToolResult Run(Func<object> body, CancellationToken cancellationToken)
    {
        ToolGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = Ok(body());
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErrorForClient(ex);
        }
        finally
        {
            ToolGate.Release();
        }
    }

    private static async Task<CallToolResult> RunAsync(Func<Task<CallToolResult>> body, CancellationToken cancellationToken)
    {
        await ToolGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await body();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErrorForClient(ex);
        }
        finally
        {
            ToolGate.Release();
        }
    }

    private static CallToolResult Ok(object value) => JsonResult(value, isError: false);

    private static CallToolResult JsonResult(object value, bool isError) => new()
    {
        IsError = isError,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, Json) }],
    };

    private static string? GetSafeMissingFileName(FileNotFoundException ex)
    {
        if (!string.IsNullOrWhiteSpace(ex.FileName))
        {
            return Path.GetFileName(ex.FileName);
        }

        const string subtitlePrefix = "Subtitle file not found: ";
        if (ex.Message.StartsWith(subtitlePrefix, StringComparison.Ordinal))
        {
            var path = ex.Message[subtitlePrefix.Length..].Trim();
            var fileName = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
        }

        return null;
    }

    private static CallToolResult ErrorForClient(Exception ex)
    {
        string message;
        switch (ex)
        {
            case McpException:
                message = ex.Message;
                break;
            case FileNotFoundException fileNotFound:
                var missingFileName = GetSafeMissingFileName(fileNotFound);
                message = missingFileName is null ? "File not found." : $"File not found: {missingFileName}";
                break;
            case DirectoryNotFoundException:
                message = "Directory not found.";
                break;
            case UnauthorizedAccessException:
                message = "Access denied.";
                break;
            default:
                Console.Error.WriteLine(ex);
                message = "The operation failed. See the seconv MCP server log for details.";
                break;
        }

        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }],
        };
    }
}
