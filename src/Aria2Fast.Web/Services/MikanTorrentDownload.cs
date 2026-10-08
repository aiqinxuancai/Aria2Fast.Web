using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

public static class MikanTorrentDownload
{
    public static bool Matches(Uri uri, WebSettings settings) => uri.Scheme is "http" or "https"
        && uri.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)
        && (new[] { "mikanani.tv", "mikanani.me", "mikanime.tv" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || uri.Authority.Equals(Validation.HttpUrl(settings.MikanBaseUrl).Authority, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(settings.MikanFallbackUrl) && uri.Authority.Equals(Validation.HttpUrl(settings.MikanFallbackUrl).Authority, StringComparison.OrdinalIgnoreCase)));

    public static async Task<byte[]> Fetch(Uri source, WebSettings settings, HttpGateway http, CancellationToken ct)
    {
        var urls = new List<Uri> { source };
        if (!string.IsNullOrWhiteSpace(settings.MikanFallbackUrl))
        {
            var backup = new UriBuilder(Validation.HttpUrl(settings.MikanFallbackUrl)) { Path = source.AbsolutePath, Query = source.Query, Fragment = "" }.Uri;
            if (backup != source) urls.Add(backup);
        }
        var failures = new List<string>();
        foreach (var url in urls)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using var client = http.Create(timeoutSeconds: 20);
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await stream.ReadAsync(chunk, timeout.Token)) > 0)
                {
                    if (buffer.Length + count > 16 * 1024 * 1024) throw new InvalidDataException("种子超过 16 MB 限制");
                    buffer.Write(chunk, 0, count);
                }
                var bytes = buffer.ToArray();
                Validate(bytes);
                return bytes;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
            { failures.Add($"{url.Authority}：{(ex is OperationCanceledException ? "下载超时" : ex.Message)}"); }
        }
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException("种子下载失败（已尝试所有配置站点）：" + string.Join("；", failures));
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
