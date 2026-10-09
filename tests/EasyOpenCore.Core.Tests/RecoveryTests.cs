using System.Security.Cryptography;
using EasyOpenCore.Core.Recovery;

namespace EasyOpenCore.Core.Tests;

public class RecoveryTests
{
    private const string UrlsSample = """
        Usage note:
        - Windows: replace `./macrecovery.py` with `macrecovery.bat`.

        Big Sur
        ./macrecovery.py -b Mac-2BD1B31983FE1663 -m 00000000000000000 download

        Tahoe
        ./macrecovery.py -b Mac-CFF7D910A743CAAF -m 00000000000000000 -os latest download

        Diagnostics
        ./macrecovery.py -b Mac-7BA5B2D9E42DDD94 -m 00000000000000000 -diag download
        """;

    [Fact]
    public void ParsesRecoveryBoards()
    {
        var boards = RecoveryDownloader.ParseBoards(UrlsSample);

        Assert.Equal(["Big Sur", "Tahoe"], boards.Select(b => b.Name));
        Assert.Equal("Mac-2BD1B31983FE1663", boards[0].BoardId);
        Assert.Equal("default", boards[0].OsType);
        Assert.Equal("latest", boards[1].OsType);
    }

    /// <summary>
    /// Talks to osrecovery.apple.com: session, image info, signed chunklist and the first image chunk.
    /// Opt-in (set EOC_NETWORK_TESTS=1) because it needs the network and a cached OpenCorePkg release.
    /// </summary>
    [Fact]
    public async Task DownloadsAndVerifiesChunklistFromApple()
    {
        if (Environment.GetEnvironmentVariable("EOC_NETWORK_TESTS") != "1")
            return;

        var macrecovery = Directory.EnumerateDirectories(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EasyOpenCore", "cache"),
                "macrecovery", SearchOption.AllDirectories).First();
        using var downloader = new RecoveryDownloader(macrecovery);
        var board = downloader.BoardFor("Sequoia")!;

        var info = await downloader.GetImageInfoAsync(board);
        var dir = Path.Combine(Path.GetTempPath(), "eoc-recovery-test");
        var (_, chunks) = await downloader.DownloadChunklistAsync(info, dir);
        Assert.NotEmpty(chunks);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, info.ImageUrl);
        request.Headers.Add("Cookie", "AssetToken=" + info.ImageToken);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, chunks[0].Size - 1);
        request.Headers.UserAgent.ParseAdd("InternetRecovery/1.0");
        var first = await (await http.SendAsync(request)).Content.ReadAsByteArrayAsync();

        Assert.Equal((int)chunks[0].Size, first.Length);
        Assert.Equal(chunks[0].Sha256, SHA256.HashData(first));
    }
}
