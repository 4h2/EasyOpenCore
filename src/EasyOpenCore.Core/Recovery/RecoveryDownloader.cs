using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace EasyOpenCore.Core.Recovery;

/// <summary>Board/MLB pair for one macOS release, as listed in OpenCorePkg's recovery_urls.txt.</summary>
public sealed record RecoveryBoard(string Name, string BoardId, string Mlb, string OsType);

public sealed record RecoveryImageInfo(string Product, string ImageUrl, string ImageToken, string ChunklistUrl, string ChunklistToken);

public sealed record Chunk(uint Size, byte[] Sha256);

public sealed record DownloadProgress(string File, long Received, long Total);

/// <summary>
/// C# port of OpenCorePkg's Utilities/macrecovery/macrecovery.py: asks Apple's recovery server
/// for a BaseSystem image, downloads it and verifies it against the signed chunklist.
/// </summary>
public sealed partial class RecoveryDownloader : IDisposable
{
    private const string Server = "http://osrecovery.apple.com";
    private readonly HttpClient _http;
    private readonly BigInteger _appleKey;

    /// <param name="macrecoveryDirectory">Utilities/macrecovery from the OpenCorePkg release (board list and Apple's public key).</param>
    public RecoveryDownloader(string macrecoveryDirectory)
    {
        Boards = ParseBoards(File.ReadAllText(Path.Combine(macrecoveryDirectory, "recovery_urls.txt")));
        _appleKey = ParsePublicKey(File.ReadAllText(Path.Combine(macrecoveryDirectory, "macrecovery.py")));
        _http = new HttpClient(new HttpClientHandler { UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("InternetRecovery/1.0");
    }

    public IReadOnlyList<RecoveryBoard> Boards { get; }

    public RecoveryBoard? BoardFor(string macosName) =>
        Boards.FirstOrDefault(b => b.Name.Equals(macosName, StringComparison.OrdinalIgnoreCase));

    // ---------- protocol ----------

    public async Task<RecoveryImageInfo> GetImageInfoAsync(RecoveryBoard board, CancellationToken ct = default)
    {
        using var sessionResponse = await _http.GetAsync(Server + "/", ct);
        var session = sessionResponse.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.SelectMany(c => c.Split("; ")).FirstOrDefault(c => c.StartsWith("session="))
            : null;
        if (session is null)
            throw new InvalidOperationException("Apple's recovery server returned no session cookie.");

        var post = new StringBuilder()
            .Append("cid=").Append(RandomHex(16)).Append('\n')
            .Append("sn=").Append(board.Mlb).Append('\n')
            .Append("bid=").Append(board.BoardId).Append('\n')
            .Append("k=").Append(RandomHex(64)).Append('\n')
            .Append("fg=").Append(RandomHex(64)).Append('\n')
            .Append("os=").Append(board.OsType);

        // The server is strict about headers: plain "text/plain" (no charset) and Connection: close, as macrecovery.py sends.
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(post.ToString()));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        using var request = new HttpRequestMessage(HttpMethod.Post, Server + "/InstallationPayload/RecoveryImage") { Content = content };
        request.Headers.Add("Cookie", session);
        request.Headers.ConnectionClose = true;
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var info = (await response.Content.ReadAsStringAsync(ct))
            .Split('\n')
            .Select(l => l.Split(": ", 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1].Trim());

        string Get(string key) => info.TryGetValue(key, out var v) ? v : throw new InvalidOperationException($"Recovery server answer is missing {key}.");
        return new RecoveryImageInfo(Get("AP"), Get("AU"), Get("AT"), Get("CU"), Get("CT"));
    }

    /// <summary>Downloads the chunklist and the image into <paramref name="directory"/> and verifies them.</summary>
    public async Task<(string Dmg, string Chunklist)> DownloadAsync(RecoveryImageInfo info, string directory,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var (chunklist, chunks) = await DownloadChunklistAsync(info, directory, progress, ct);
        var dmg = Path.Combine(directory, FileNameOf(info.ImageUrl));

        // Reuse a previous complete download when it still verifies.
        if (!File.Exists(dmg) || !await VerifyImageAsync(dmg, chunks, ct))
        {
            await SaveAsync(info.ImageUrl, info.ImageToken, dmg, progress, ct);
            if (!await VerifyImageAsync(dmg, chunks, ct))
                throw new InvalidDataException("The recovery image does not match its chunklist.");
        }
        return (dmg, chunklist);
    }

    /// <summary>Downloads and verifies only the (small) signed chunklist.</summary>
    public async Task<(string Path, List<Chunk> Chunks)> DownloadChunklistAsync(RecoveryImageInfo info, string directory,
        IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileNameOf(info.ChunklistUrl));
        await SaveAsync(info.ChunklistUrl, info.ChunklistToken, path, progress, ct);
        return (path, ReadChunklist(await File.ReadAllBytesAsync(path, ct)));
    }

    private async Task SaveAsync(string url, string token, string path, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Cookie", "AssetToken=" + token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;

        var temp = path + ".part";
        await using (var input = await response.Content.ReadAsStreamAsync(ct))
        await using (var output = File.Create(temp))
        {
            var buffer = new byte[1 << 20];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                progress?.Report(new DownloadProgress(Path.GetFileName(path), received, total));
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    // ---------- chunklist ----------

    /// <summary>Parses a chunklist and checks Apple's RSA signature (macrecovery.py verify_chunklist).</summary>
    public List<Chunk> ReadChunklist(byte[] data)
    {
        const int headerSize = 0x24, chunkSize = 0x24;
        if (data.Length < headerSize || Encoding.ASCII.GetString(data, 0, 4) != "CNKL")
            throw new InvalidDataException("Not a chunklist.");

        uint declaredHeader = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        byte fileVersion = data[8], chunkMethod = data[9], signatureMethod = data[10];
        ulong count = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(12));
        ulong chunkOffset = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(20));
        ulong signatureOffset = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(28));
        if (declaredHeader != headerSize || fileVersion != 1 || chunkMethod != 1 || count == 0
            || chunkOffset != headerSize || signatureOffset != chunkOffset + chunkSize * count)
            throw new InvalidDataException("Unexpected chunklist layout.");

        var chunks = new List<Chunk>((int)count);
        for (ulong i = 0; i < count; i++)
        {
            int at = (int)(chunkOffset + i * chunkSize);
            chunks.Add(new Chunk(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at)), data.AsSpan(at + 4, 32).ToArray()));
        }

        var digest = SHA256.HashData(data.AsSpan(0, (int)signatureOffset));
        if (signatureMethod != 1 || data.Length != (int)signatureOffset + 256)
            throw new InvalidDataException("The chunklist is not signed by Apple.");

        // PKCS#1 v1.5 with SHA-256, checked exactly like macrecovery.py does.
        var signature = new BigInteger(data.AsSpan((int)signatureOffset, 256), isUnsigned: true, isBigEndian: false);
        var expected = BigInteger.Parse("01" + new string('F', 404) + "003031300D060960864801650304020105000420" + new string('0', 64), NumberStyles.HexNumber)
                       | new BigInteger(digest, isUnsigned: true, isBigEndian: true);
        if (BigInteger.ModPow(signature, 0x10001, _appleKey) != expected)
            throw new InvalidDataException("Invalid chunklist signature.");
        return chunks;
    }

    public static async Task<bool> VerifyImageAsync(string dmg, List<Chunk> chunks, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(dmg);
        if (file.Length != chunks.Sum(c => (long)c.Size))
            return false;
        foreach (var chunk in chunks)
        {
            var buffer = new byte[chunk.Size];
            await file.ReadExactlyAsync(buffer, ct);
            if (!SHA256.HashData(buffer).AsSpan().SequenceEqual(chunk.Sha256))
                return false;
        }
        return true;
    }

    // ---------- helpers ----------

    private static string FileNameOf(string url) => Path.GetFileName(new Uri(url).LocalPath);

    private static string RandomHex(int length) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(length / 2));

    /// <summary>Section title followed by "./macrecovery.py -b BOARD -m MLB [-os TYPE] download" lines; the first one wins.</summary>
    public static List<RecoveryBoard> ParseBoards(string text)
    {
        var boards = new List<RecoveryBoard>();
        string? section = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
                continue;
            var cmd = CommandLine().Match(line);
            if (!cmd.Success)
            {
                section = line.TrimEnd(':');
                continue;
            }
            if (section is null || boards.Any(b => b.Name == section) || line.Contains("-diag"))
                continue;
            boards.Add(new RecoveryBoard(section, cmd.Groups["b"].Value, cmd.Groups["m"].Value,
                cmd.Groups["os"].Success ? cmd.Groups["os"].Value : "default"));
        }
        return boards;
    }

    private static BigInteger ParsePublicKey(string macrecoveryPy)
    {
        var m = PublicKey().Match(macrecoveryPy);
        if (!m.Success)
            throw new InvalidDataException("Apple's public key was not found in macrecovery.py.");
        return BigInteger.Parse("0" + m.Groups[1].Value, NumberStyles.HexNumber);
    }

    [GeneratedRegex(@"macrecovery\.py\s+-b\s+(?<b>Mac-[0-9A-F]+)\s+-m\s+(?<m>[0-9A-Z]{17})(?:\s+-os\s+(?<os>\w+))?", RegexOptions.IgnoreCase)]
    private static partial Regex CommandLine();

    [GeneratedRegex(@"Apple_EFI_ROM_public_key_1\s*=\s*0x([0-9A-Fa-f]+)")]
    private static partial Regex PublicKey();

    public void Dispose() => _http.Dispose();
}
