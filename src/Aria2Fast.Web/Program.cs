using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Aria2Fast.Web.Endpoints;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null
});
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://0.0.0.0:8080");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 24 * 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<StateStore>();
builder.Services.AddSingleton<HttpGateway>();
builder.Services.AddSingleton<AriaRpc>();
builder.Services.AddSingleton<DownloadPaths>();
builder.Services.AddSingleton<FeedService>();
builder.Services.AddSingleton<AiService>();
builder.Services.AddSingleton<MikanService>();
builder.Services.AddSingleton<SubscriptionService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService<OssSyncWorker>();
builder.Services.AddSingleton<FileService>();
builder.Services.AddSingleton<LocalAriaService>();
builder.Services.AddHostedService(p => p.GetRequiredService<LocalAriaService>());
builder.Services.AddSingleton<BackgroundWorker>();
builder.Services.AddHostedService(p => p.GetRequiredService<BackgroundWorker>());
builder.Services.AddMemoryCache();
var data = Path.GetFullPath(builder.Configuration["ARIA2FAST_DATA_DIR"] ?? Path.Combine(AppContext.BaseDirectory, "data"));
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(data, "keys"))).SetApplicationName("Aria2Fast.Web");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "aria2fast.session";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromDays(7);
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    o.Events.OnValidatePrincipal = c =>
    {
        var hash = c.HttpContext.RequestServices.GetRequiredService<StateStore>().Read().PasswordHash;
        if (c.Principal?.FindFirst("version")?.Value != AuthEndpoints.Version(hash)) c.RejectPrincipal();
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
AuthEndpoints.Initialize(app.Services.GetRequiredService<StateStore>(), builder.Configuration);
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' https: http: data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS") && context.Request.Headers["X-Aria2Fast"] != "1")
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { error = "缺少请求验证头" }); return; }
    }
    try { await next(); }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "请求失败 {Path}", context.Request.Path);
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = ex is ArgumentException or System.Text.Json.JsonException ? 400 : ex is FileNotFoundException ? 404 : 502;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapAuth();
var api = app.MapGroup("/api").RequireAuthorization();
api.MapDownloads();
api.MapConfiguration();
api.MapSubscriptions();
api.MapAnime();
api.MapFiles();
app.Map("/api/{**path}", () => Results.NotFound(new { error = "接口不存在" }));
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
