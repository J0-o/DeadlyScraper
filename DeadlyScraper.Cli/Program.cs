using System.Text.Json;
using DeadlyScraper;

if (args.Length == 1 && string.Equals(args[0], "--help", StringComparison.OrdinalIgnoreCase))
{
    PrintUsage();
    return 0;
}

if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
{
    PrintUsage();
    return 1;
}

var url = args[0].Trim();
if (!DeadlyStreamClient.CanHandle(url))
{
    Console.Error.WriteLine("The URL must point to deadlystream.com.");
    return 2;
}

string? versionLabel = null;
string? fileName = null;
string? downloadDirectory = null;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--version":
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                Console.Error.WriteLine("--version requires a version label.");
                return 1;
            }

            versionLabel = args[++i];
            break;

        case "--file":
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                Console.Error.WriteLine("--file requires an exact file name.");
                return 1;
            }

            fileName = args[++i];
            break;

        case "--download":
            if (i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                Console.Error.WriteLine("--download requires a destination directory.");
                return 1;
            }

            downloadDirectory = args[++i];
            break;

        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 1;
    }
}

try
{
    if (!string.IsNullOrWhiteSpace(fileName) && string.IsNullOrWhiteSpace(downloadDirectory))
    {
        Console.Error.WriteLine("--file can only be used with --download.");
        return 1;
    }

    var client = new DeadlyStreamClient();
    var jsonOptions = new JsonSerializerOptions { WriteIndented = true };

    var metadata = string.IsNullOrWhiteSpace(versionLabel)
        ? await client.GetMetadataAsync(url)
        : await client.GetMetadataForVersionAsync(url, versionLabel);

    if (string.IsNullOrWhiteSpace(downloadDirectory))
    {
        Console.WriteLine(JsonSerializer.Serialize(metadata, jsonOptions));
        return 0;
    }

    var progress = new Progress<int>(percent => Console.Error.WriteLine($"Progress: {percent}%"));
    var results = await client.DownloadFilesAsync(metadata, downloadDirectory, fileName, progress);

    var output = new
    {
        Metadata = metadata,
        Downloads = results
    };

    Console.WriteLine(JsonSerializer.Serialize(output, jsonOptions));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"DeadlyStream operation failed: {ex.Message}");
    return 3;
}

static void PrintUsage()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  DeadlyScraper.Cli <deadlystream file page url> [options]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --help                         Show this help text.");
    Console.WriteLine("  --version <version label>      Look up a specific version instead of the current version.");
    Console.WriteLine("  --file <exact file name>       Download only the first matching file.");
    Console.WriteLine("  --download <directory>         Download the selected file(s) to a directory.");
}
