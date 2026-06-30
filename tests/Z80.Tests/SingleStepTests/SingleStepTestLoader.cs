using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Z80.Tests.SingleStepTests;

/// <summary>
/// Resolves and loads SingleStepTests/z80 v1 JSON files by opcode-page name (e.g.
/// "00", "cb 00", "dd e6"). Files are cached under SingleStepTests/data/ next to
/// this source file (gitignored) and downloaded from the upstream repo on first
/// use, so the data set never needs to be vendored or submoduled.
/// </summary>
public static class SingleStepTestLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    // This network's IPv6 path fails TLS handshakes to GitHub's CDN; force IPv4.
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectCallback = async (context, cancellationToken) =>
        {
            var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, cancellationToken);
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(entry.AddressList[0], context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    });

    private static string DataDir([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "data");

    public static string DataPath(string name) => Path.Combine(DataDir(), $"{name}.json");

    public static List<TestCase> Load(string name)
    {
        var path = DataPath(name);
        EnsureDownloaded(name, path);
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<TestCase>>(stream, Options)
            ?? throw new InvalidDataException($"'{path}' did not contain a JSON test array.");
    }

    private static void EnsureDownloaded(string name, string path)
    {
        if (File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var url = $"https://raw.githubusercontent.com/SingleStepTests/z80/main/v1/{Uri.EscapeDataString(name)}.json";
        var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        File.WriteAllBytes(path, bytes);
    }
}
