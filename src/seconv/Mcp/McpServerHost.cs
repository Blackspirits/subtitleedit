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
        "Paths are restricted to explicitly approved read/write directories. " +
        "This path policy is not an OS filesystem sandbox; apply client approval and OS permissions.";

    public static async Task<int> RunAsync(string[] args)
    {
        if (!TryParseArguments(args, out var verbose, out var readDirs, out var writeDirs, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        try
        {
            // Fail closed before opening the protocol transport. Each stdio server
            // process receives an explicit, immutable set of readable/writable roots.
            SubtitleTools.ConfigurePathPolicy(new McpPathPolicy(readDirs, writeDirs));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
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

    internal static bool TryParseArguments(string[] args, out bool verbose,
        out string[] readDirs, out string[] writeDirs, out string? error)
    {
        verbose = false;
        error = null;
        var reads = new List<string>();
        var writes = new List<string>();
        readDirs = [];
        writeDirs = [];

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-v", StringComparison.OrdinalIgnoreCase))
            {
                verbose = true;
                continue;
            }

            var isRead = arg.Equals("--read-dir", StringComparison.OrdinalIgnoreCase);
            var isWrite = arg.Equals("--write-dir", StringComparison.OrdinalIgnoreCase);
            if (isRead || isWrite)
            {
                if (++i >= args.Length || string.IsNullOrWhiteSpace(args[i]) ||
                    args[i].StartsWith('-'))
                {
                    error = $"Missing directory after '{arg}'. Usage: seconv mcp --read-dir <folder> [--write-dir <folder>]";
                    return false;
                }

                (isRead ? reads : writes).Add(args[i]);
                continue;
            }

            error = $"Unknown mcp option '{arg}'. Usage: seconv mcp --read-dir <folder> [--write-dir <folder>] [--verbose]";
            return false;
        }

        if (reads.Count == 0)
        {
            error = "A --read-dir directory is required. MCP filesystem access is denied by default.";
            return false;
        }

        readDirs = reads.ToArray();
        writeDirs = writes.ToArray();
        return true;
    }
}
