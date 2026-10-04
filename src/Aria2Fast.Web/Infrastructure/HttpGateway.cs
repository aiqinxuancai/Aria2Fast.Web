using System.Net;
using System.Text;

namespace Aria2Fast.Web.Infrastructure;

public sealed class HttpGateway(StateStore store)
{
    public HttpClient Create(bool useProxy = true, int timeoutSeconds = 40)
    {
        var proxy = useProxy ? store.Read().Settings.ProxyUrl : "";
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        if (!string.IsNullOrWhiteSpace(proxy)) handler.Proxy = new WebProxy(Validation.HttpUrl(proxy));
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds), MaxResponseContentBufferSize = 16 * 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aria2Fast-Web/1.0");
        return client;
    }

    public async Task<string> GetTextAsync(string url, CancellationToken ct = default)
    {
        using var client = Create();
        using var response = await client.GetAsync(Validation.HttpUrl(url), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, ct)) > 0)
        {
            if (buffer.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("远端内容超过 8 MB 限制");
            buffer.Write(bytes, 0, count);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
