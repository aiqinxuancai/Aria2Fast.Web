using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Aria2Fast.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Aria2Fast.Web.Endpoints;

public static class AuthEndpoints
{
    private static readonly PasswordHasher<string> Hasher = new();
    public static string Version(string hash) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hash)));
    public static void Initialize(StateStore store, IConfiguration config)
    {
        var supplied = config["ARIA2FAST_PASSWORD"];
        if (!string.IsNullOrWhiteSpace(supplied))
        {
            if (supplied.Length < 12) throw new ArgumentException("ARIA2FAST_PASSWORD 至少需要 12 个字符");
            var old = store.Read().PasswordHash;
            if (old.Length == 0 || Hasher.VerifyHashedPassword("admin", old, supplied) == PasswordVerificationResult.Failed)
                store.Update(s => s.PasswordHash = Hasher.HashPassword("admin", supplied));
            return;
        }
        if (store.Read().PasswordHash.Length > 0) return;
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        var path = Path.Combine(store.DataDirectory, "initial-password.txt");
        File.WriteAllText(path, password);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        store.Update(s => s.PasswordHash = Hasher.HashPassword("admin", password));
        Console.WriteLine($"首次登录密码已保存至 {path}，登录后请在设置中修改。");
    }
    public static void MapAuth(this WebApplication app)
    {
        app.MapGet("/api/auth", (HttpContext c) => new { authenticated = c.User.Identity?.IsAuthenticated == true });
        app.MapPost("/api/auth/login", async (LoginRequest input, HttpContext c, StateStore store) =>
        {
            var hash = store.Read().PasswordHash;
            if (string.IsNullOrEmpty(input.Password) || input.Password.Length > 1024 || Hasher.VerifyHashedPassword("admin", hash, input.Password) == PasswordVerificationResult.Failed)
                return Results.Json(new { error = "密码不正确" }, statusCode: 401);
            var identity = new ClaimsIdentity([new(ClaimTypes.Name, "admin"), new("version", Version(hash))], CookieAuthenticationDefaults.AuthenticationScheme);
            await c.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("login");
        app.MapPost("/api/auth/logout", async (HttpContext c) => { await c.SignOutAsync(); return Results.Ok(); }).RequireAuthorization();
        app.MapPost("/api/auth/password", (PasswordRequest input, StateStore store, IConfiguration configuration) =>
        {
            if (!string.IsNullOrWhiteSpace(configuration["ARIA2FAST_PASSWORD"])) throw new ArgumentException("密码由 ARIA2FAST_PASSWORD 环境变量管理，请修改环境变量后重启");
            if (Hasher.VerifyHashedPassword("admin", store.Read().PasswordHash, input.CurrentPassword) == PasswordVerificationResult.Failed) return Results.BadRequest(new { error = "当前密码不正确" });
            if (input.NewPassword.Length is < 12 or > 1024) throw new ArgumentException("新密码长度须为 12 至 1024 个字符");
            store.Update(s => s.PasswordHash = Hasher.HashPassword("admin", input.NewPassword));
            var path = Path.Combine(store.DataDirectory, "initial-password.txt");
            if (File.Exists(path)) File.Delete(path);
            return Results.Ok(new { ok = true });
        }).RequireAuthorization().RequireRateLimiting("login");
    }
    public sealed record LoginRequest(string Password);
    public sealed record PasswordRequest(string CurrentPassword, string NewPassword);
}
