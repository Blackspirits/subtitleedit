using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SeConv.Helpers;

namespace SeConv.Mcp;

/// <summary>
/// <c>seconv mcp</c>: runs seconv as a Model Context Protocol server over stdio so an MCP client
/// (Claude Desktop, Claude Code, Cursor, ...) can call the tools in <see cref="SubtitleTools"/>
/// without a shell. The JSON-RPC transport owns stdout; logs and any incidental console output
/// from libse or Spectre go to stderr so they can never corrupt a protocol frame.
/// </summary>
internal static class McpServerHost
{
    private const string Instructions =
        "seconv exposes Subtitle Edit's conversion engine for hundreds of subtitle formats. " +
        "Start with subtitle_info to detect a file's format, encoding and duration; use read_subtitle to see " +
        "its paragraphs (any format, paged); lint_subtitle to find timing/line-length problems; " +
        "convert_subtitle to write a new file in another format, optionally shifting times, changing " +
        "frame rate or applying operations such as FixCommonErrors. Paths are local file-system paths. " +
        "The server does not add a filesystem sandbox; access is limited by the operating-system permissions of the seconv process.";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArguments(args, out var verbose, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        // Stdout is the protocol channel. The stdio transport writes to the raw standard output
        // stream, so redirecting Console.Out only affects incidental writers (libse notices,
        // Spectre markup from a non-quiet code path) - they land on stderr instead of inside a frame.
        Console.SetOut(Console.Error);

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "seconv", Version = CliSchema.Version };
                options.ServerInstructions = Instructions;
            })
            .WithStdioServerTransport()
            .WithTools<SubtitleTools>();

        await builder.Build().RunAsync();
        return 0;
    }

    internal static bool TryParseArguments(string[] args, out bool verbose, out string? error)
    {
        verbose = false;
        error = null;

        foreach (var arg in args)
        {
            if (arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
                continue;
            }

            error = $"Unknown mcp option '{arg}'. Usage: seconv mcp [--verbose]";
            return false;
        }

        return true;
    }
}
