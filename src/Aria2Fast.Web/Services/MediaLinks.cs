using System.Security.Cryptography;
using Aria2Fast.Web.Endpoints;
using Aria2Fast.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;

namespace Aria2Fast.Web.Services;

/// <summary>
/// Signed, expiring links to one video for external players, which cannot send the login cookie.
/// Changing the access password revokes every link issued before.
/// </summary>
public sealed class MediaLinks(IDataProtectionProvider provider, StateStore store)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    private readonly ITimeLimitedDataProtector protector = provider.CreateProtector("Aria2Fast.Library.Media.v1").ToTimeLimitedDataProtector();

    public string Token(string episodeId) => protector.Protect(episodeId + "|" + PasswordVersion(), Lifetime);

    public string? EpisodeId(string token)
    {
        try
        {
            var parts = protector.Unprotect(token).Split('|');
            return parts.Length == 2 && parts[1] == PasswordVersion() ? parts[0] : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }

    private string PasswordVersion() => AuthEndpoints.Version(store.Read().PasswordHash)[..16];
}
