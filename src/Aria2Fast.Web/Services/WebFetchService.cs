using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Aria2Fast.Web.Services;

public sealed class WebFetchService
{
    public const int MaxResponseBytes = 1024 * 1024;
    public static WebFetchService Shared { get; } = new(CreateClient());
    private readonly HttpClient _client;

    public WebFetchService(HttpClient client) => _client = client;

    public sealed record FetchResult(string url, string final_url, int status_code,
        string content_type, string title, string content, bool truncated, bool ok, string error);

    public static string DescribeError(Exception exception)
    {
        SocketException? socket = null;
        HttpRequestException? http = null;
        var details = new System.Collections.Generic.List<object>();
        for (Exception? current = exception; current != null; current = current.InnerException)
        {
            socket ??= current as SocketException;
            http ??= current as HttpRequestException;
            details.Add(new { type = current.GetType().Name, message = current.Message, hresult = current.HResult });
        }
        string category = exception is OperationCanceledException ? "timeout"
            : http?.HttpRequestError == HttpRequestError.NameResolutionError
                || socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain ? "dns_error"
            : http?.HttpRequestError == HttpRequestError.SecureConnectionError ? "tls_error"
            : socket != null ? "connection_error"
            : exception is ArgumentException ? "invalid_url_or_argument" : "fetch_error";
        return JsonSerializer.Serialize(new { ok = false, error = new {
            category, message = exception.Message, status_code = (int?)http?.StatusCode,
            http_request_error = http?.HttpRequestError.ToString(),
            socket_error_code = socket?.SocketErrorCode.ToString(), native_error_code = socket?.NativeErrorCode,
            details
        }});
    }

    public async Task<FetchResult> FetchAsync(string url, int? maxChars = null,
        CancellationToken cancellationToken = default)
    {
        var uri = ValidateUrl(url);
        int limit = Math.Clamp(maxChars ?? 12000, 1, 30000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        for (int redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.8,*/*;q=0.7");
            request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");
            request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            int status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                if (redirects >= 5) throw new InvalidOperationException("Too many URL redirects (maximum 5).");
                var location = response.Headers.Location ?? throw new InvalidOperationException("Redirect has no Location header.");
                uri = ValidateUrl(new Uri(uri, location).AbsoluteUri);
                continue;
            }
            var type = response.Content.Headers.ContentType;
            string mediaType = type?.MediaType?.ToLowerInvariant() ?? "";
            if (!(mediaType.StartsWith("text/") || mediaType is "application/json" or "application/xml"
                or "application/xhtml+xml" || mediaType.EndsWith("+json") || mediaType.EndsWith("+xml")))
                return new FetchResult(url, uri.AbsoluteUri, status, mediaType, "", "", false, false,
                    $"HTTP {status} {response.ReasonPhrase}; unsupported content type. Only HTML and text content can be fetched.");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new InvalidOperationException("Response exceeds the 1 MiB download limit.");

            using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(chunk.AsMemory(), timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaxResponseBytes)
                    throw new InvalidOperationException("Response exceeds the 1 MiB download limit.");
                buffer.Write(chunk, 0, read);
            }
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = string.IsNullOrWhiteSpace(type?.CharSet) ? Encoding.UTF8
                : Encoding.GetEncoding(type.CharSet.Trim('"', '\''));
            buffer.Position = 0;
            using var reader = new StreamReader(buffer, encoding, detectEncodingFromByteOrderMarks: true);
            string content = await reader.ReadToEndAsync(timeout.Token);
            string title = "";
            if (mediaType is "text/html" or "application/xhtml+xml")
            {
                title = Regex.Match(content, @"<title\b[^>]*>(.*?)</title\s*>",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1)).Groups[1].Value;
                title = CleanHtml(title);
                content = CleanHtml(content);
            }
            bool truncated = content.Length > limit;
            return new FetchResult(url, uri.AbsoluteUri, status, mediaType,
                title.Length > 500 ? title[..500] : title, truncated ? content[..limit] : content, truncated,
                response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "" : $"HTTP {status} {response.ReasonPhrase}");
        }
    }

    private static string CleanHtml(string html)
    {
        string Replace(string input, string pattern, string replacement) => Regex.Replace(input, pattern,
            replacement, RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
        html = Replace(html, @"<!--.*?-->|<(script|style|noscript|template)\b[^>]*>.*?</\1\s*>", " ");
        html = Replace(html, @"<[^>]+>", " ");
        return Replace(WebUtility.HtmlDecode(html), @"\s+", " ").Trim();
    }

    public static Uri ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Provide an absolute HTTP/HTTPS URL without credentials.");
        if (uri.IsLoopback || (IPAddress.TryParse(uri.DnsSafeHost, out var address) && !IsPublicAddress(address)))
            throw new ArgumentException("Only public Internet URLs are allowed.");
        return uri;
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && b[1] == 168) && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 198 && b[1] is 18 or 19);
        // Global unicast only; exclude transition addresses which embed IPv4 destinations.
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (b[0] & 0xe0) == 0x20
            && !(b[0] == 0x20 && b[1] == 0x02)
            && !(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0);
    }

    public static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = async (context, token) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
                if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
                    throw new HttpRequestException("Only public Internet addresses are allowed.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    // Connect to the validated addresses, avoiding a second DNS resolution.
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
