using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

/// <summary>A prefetched download: torrent bytes, or the magnet link the site redirected to.</summary>
public sealed record PrefetchedTorrent(byte[]? Data, string? Magnet);

/// <summary>Raised when every candidate site failed. <see cref="Unreachable"/> means no site could be connected to at all.</summary>
public sealed class TorrentPrefetchException(string message, bool unreachable) : InvalidOperationException(message)
{
    public bool Unreachable { get; } = unreachable;
}

/// <summary>Fetches torrents on the server through ProxyUrl, follows redirects to magnet links and rejects error pages before aria2 sees them.</summary>
public sealed class TorrentDownloadService(HttpGateway http)
{
    public const int MaxBytes = 16 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly string[] MikanHosts = ["mikanani.tv", "mikanani.me", "mikanime.tv"];

    public static bool IsMikan(Uri uri, WebSettings settings) => uri.Scheme is "http" or "https"
        && uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
        && (MikanHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) || SameSite(uri, settings.MikanBaseUrl) || SameSite(uri, settings.MikanFallbackUrl));

    /// <summary>Mikan torrents are always prefetched; other .torrent links and Torznab indexer links follow the ProxyTorrentDownloads switch.</summary>
    public static bool Matches(Uri uri, WebSettings settings) => uri.Scheme is "http" or "https"
        && (IsMikan(uri, settings) || (settings.ProxyTorrentDownloads
            && (uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) || settings.AnimeSources.Torznab.Any(e => SameSite(uri, e.Url)))));

    private static bool SameSite(Uri uri, string? site) =>
        !string.IsNullOrWhiteSpace(site) && Uri.TryCreate(site, UriKind.Absolute, out var other) && uri.Authority.Equals(other.Authority, StringComparison.OrdinalIgnoreCase);

    public async Task<PrefetchedTorrent> Fetch(Uri source, WebSettings settings, CancellationToken ct)
    {
        var urls = new List<Uri> { source };
        if (IsMikan(source, settings) && !string.IsNullOrWhiteSpace(settings.MikanFallbackUrl))
        {
            var backup = new UriBuilder(Validation.HttpUrl(settings.MikanFallbackUrl)) { Path = source.AbsolutePath, Query = source.Query, Fragment = "" }.Uri;
            if (backup != source) urls.Add(backup);
        }
        var failures = new List<string>();
        var unreachable = true;
        foreach (var url in urls)
        {
            try { return await FetchOne(url, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            {
                // An HTTP status or invalid content means the site answered; only connection failures and timeouts count as unreachable.
                unreachable &= ex is OperationCanceledException or IOException || (ex is HttpRequestException { StatusCode: null });
                failures.Add($"{url.Authority}：{(ex is OperationCanceledException ? "下载超时" : ex.Message)}");
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new TorrentPrefetchException((urls.Count > 1 ? "种子下载失败（已尝试所有配置站点）：" : "种子下载失败：") + string.Join("；", failures), unreachable);
    }

    private async Task<PrefetchedTorrent> FetchOne(Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = http.Create(timeoutSeconds: 20, allowRedirect: false);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.TryGetValues("Location", out var values) && values.FirstOrDefault() is { Length: > 0 } location)
            {
                // Prowlarr, Jackett and several sites answer a download link with a redirect to the magnet link.
                if (location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return new(null, location);
                if (!Uri.TryCreate(url, location, out var next) || next.Scheme is not ("http" or "https")) throw new InvalidDataException("站点跳转到了不支持的地址");
                url = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("种子超过 16 MB 限制");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + count > MaxBytes) throw new InvalidDataException("种子超过 16 MB 限制");
                buffer.Write(chunk, 0, count);
            }
            var bytes = buffer.ToArray();
            Validate(bytes);
            return new(bytes, null);
        }
        throw new InvalidDataException("跳转次数过多");
    }

    // Validate the bencoded envelope before submitting it, including HTTP 200 pollution pages.
    public static void Validate(byte[] data)
    {
        var position = 0;
        var hasInfo = false;
        InvalidDataException Invalid() => new("返回内容不是有效种子，可能是站点错误页面或文件损坏");
        ReadOnlySpan<byte> ReadString()
        {
            var length = 0;
            var start = position;
            while (position < data.Length && data[position] is >= (byte)'0' and <= (byte)'9')
            {
                if (length > data.Length / 10) throw Invalid();
                length = length * 10 + data[position++] - '0';
            }
            if (position == start || position >= data.Length || data[position++] != ':' || length > data.Length - position) throw Invalid();
            var value = data.AsSpan(position, length);
            position += length;
            return value;
        }
        void ReadValue(int depth)
        {
            if (depth > 64 || position >= data.Length) throw Invalid();
            var kind = data[position];
            if (kind is >= (byte)'0' and <= (byte)'9') { ReadString(); return; }
            position++;
            if (kind == 'i')
            {
                if (position < data.Length && data[position] == '-') position++;
                var start = position;
                while (position < data.Length && data[position] is >= (byte)'0' and <= (byte)'9') position++;
                if (start == position || position >= data.Length || data[position++] != 'e') throw Invalid();
                return;
            }
            if (kind is not ((byte)'d') and not ((byte)'l')) throw Invalid();
            while (position < data.Length && data[position] != 'e')
            {
                if (kind == 'd')
                {
                    var key = ReadString();
                    if (depth == 0 && key.SequenceEqual("info"u8))
                        hasInfo = position < data.Length && data[position] == 'd';
                }
                ReadValue(depth + 1);
            }
            if (position >= data.Length || data[position++] != 'e') throw Invalid();
        }
        if (data.Length == 0 || data[0] != 'd') throw Invalid();
        ReadValue(0);
        if (!hasInfo || position != data.Length) throw Invalid();
    }
}
