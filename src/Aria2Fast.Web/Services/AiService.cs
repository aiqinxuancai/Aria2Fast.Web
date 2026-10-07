using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aria2Fast.Service;
using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;

namespace Aria2Fast.Web.Services;

public sealed class AiService(StateStore store, HttpGateway http)
{
    public sealed record ReviewTask(string Id, string AnimeId, string Name, string Status, string Progress, DateTimeOffset UpdatedAt);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ReviewTask> tasks = new();
    public ReviewTask[] ReviewTasks() => tasks.Values.OrderByDescending(x => x.UpdatedAt).ToArray();
    // Fixed-size lock stripes avoid retaining one semaphore for every title ever opened.
    private readonly SemaphoreSlim[] reviewGates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1)).ToArray();
    private string TranslationKey(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    public string? CachedTranslation(string text) => store.Read().Translations.GetValueOrDefault(TranslationKey(text));

    public AiReview? CachedReview(string id)
    {
        var state = store.Read();
        if (state.Reviews.TryGetValue(id, out var review)) return review;
        // Reuse persisted reviews from the previous profile-specific cache, including after changing models.
        return state.Reviews.Where(x => x.Key.StartsWith(id + ":", StringComparison.Ordinal))
            .OrderByDescending(x => x.Value.CreatedAt).Select(x => x.Value).FirstOrDefault();
    }

    public async Task<string> Translate(string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("简介为空");
        var key = TranslationKey(text);
        if (store.Read().Translations.TryGetValue(key, out var cached)) return cached;
        var translated = await Send(text, "将动漫简介翻译为自然简体中文，保留原意，不补充事实。只输出译文。", ct);
        store.Update(s => s.Translations[key] = translated);
        return translated;
    }

    public async Task<string> Send(string prompt, string instruction, CancellationToken ct = default, string? profileId = null)
    {
        var settings = store.Read().Settings;
        var profile = settings.AiProfiles.FirstOrDefault(x => x.Id == (profileId ?? settings.SelectedAiId)) ?? settings.AiProfiles.FirstOrDefault();
        return await SendProfile(profile, prompt, instruction, ct);
    }

    public async Task<string> SendProfile(AiProfile? profile, string prompt, string instruction, CancellationToken ct = default)
    {
        if (profile is null || string.IsNullOrWhiteSpace(profile.ApiKey) || string.IsNullOrWhiteSpace(profile.ModelName))
            throw new ArgumentException("请先在设置中配置 AI 服务、模型和 API Key");
        var url = AiProtocol.BuildRequestUrl(profile.Protocol, profile.BaseUrl, profile.ModelName);
        using var request = new HttpRequestMessage(HttpMethod.Post, Validation.HttpUrl(url));
        object body;
        switch (profile.Protocol)
        {
            case AiProtocolType.Claude:
                request.Headers.Add("x-api-key", profile.ApiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
                body = new { model = profile.ModelName, max_tokens = 4096, system = instruction, messages = new[] { new { role = "user", content = prompt } } };
                break;
            case AiProtocolType.Gemini:
                request.Headers.Add("x-goog-api-key", profile.ApiKey);
                body = new { system_instruction = new { parts = new[] { new { text = instruction } } }, contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } } };
                break;
            case AiProtocolType.OpenAIResponses:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
                body = new { model = profile.ModelName, instructions = instruction, input = prompt, store = false };
                break;
            default:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", profile.ApiKey);
                body = new { model = profile.ModelName, messages = new[] { new { role = "system", content = instruction }, new { role = "user", content = prompt } } };
                break;
        }
        request.Content = JsonContent.Create(body);
        using var client = http.Create(timeoutSeconds: 180);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"AI 服务返回 HTTP {(int)response.StatusCode}，请检查接口、模型和密钥");
        var json = await response.Content.ReadFromJsonAsync<JsonNode>(ct) ?? throw new InvalidDataException("AI 返回为空");
        var text = profile.Protocol switch
        {
            AiProtocolType.Claude => string.Join("\n", json["content"]!.AsArray().Where(x => x?["type"]?.ToString() == "text").Select(x => x!["text"]?.ToString())),
            AiProtocolType.Gemini => string.Join("\n", json["candidates"]?[0]?["content"]?["parts"]?.AsArray().Select(x => x?["text"]?.ToString()) ?? []),
            AiProtocolType.OpenAIResponses => string.Join("\n", json["output"]!.AsArray().Where(x => x?["type"]?.ToString() == "message").SelectMany(x => x!["content"]!.AsArray()).Where(x => x?["type"]?.ToString() == "output_text").Select(x => x!["text"]?.ToString())),
            _ => json["choices"]?[0]?["message"]?["content"]?.ToString() ?? ""
        };
        return string.IsNullOrWhiteSpace(text) ? throw new InvalidDataException("AI 没有返回文本") : text.Trim();
    }

    public async Task<AiReview> Review(string id, string name, string summary, CancellationToken ct, bool refresh = false)
    {
        var before = CachedReview(id);
        if (!refresh && before is not null) return before;
        var gate = reviewGates[(int)((uint)StringComparer.Ordinal.GetHashCode(id) % (uint)reviewGates.Length)];
        await gate.WaitAsync(ct);
        ReviewTask? task = null;
        void Progress(string message, string status = "running", string? details = null)
        {
            if (task is null) return;
            task = task with { Progress = message, Status = status, UpdatedAt = DateTimeOffset.UtcNow };
            tasks[id] = task;
            store.Log($"【{name}】{message}", status == "completed" ? "success" : status == "failed" ? "error" : "info", details);
        }
        try
        {
            var cached = CachedReview(id);
            if (cached is not null && (!refresh || cached.CreatedAt != before?.CreatedAt)) return cached;
            foreach (var old in tasks.Where(x => x.Value.Status != "running" && x.Value.UpdatedAt < DateTimeOffset.UtcNow.AddHours(-1)).ToArray())
                tasks.TryRemove(old.Key, out _);
            task = new(Guid.NewGuid().ToString("N"), id, name, "running", "开始调查与评析", DateTimeOffset.UtcNow);
            Progress("开始调查与评析");
            var settings = store.Read().Settings;
            var profile = settings.AiProfiles.FirstOrDefault(x => x.Id == settings.SelectedAiId) ?? settings.AiProfiles.FirstOrDefault();
            var search = new ResearchSearchTools(settings, () => http.Create(timeoutSeconds: 25));
            var agent = new AnimeReviewAgent((prompt, instruction, token) => SendProfile(profile, prompt, instruction, token),
                search.Providers.Count > 0 ? search.Search : null, search.Providers,
                (url, token) => WebFetchService.Shared.FetchAsync(url, 10000, token));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(8));
            AiReview review;
            try { review = await agent.Run(name, summary, deadline.Token, message => Progress(message)); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("番剧调查超过时间限制，请重试。已有评析仍保留。");
            }
            review = review with { Model = profile?.ModelName ?? "" };
            ct.ThrowIfCancellationRequested();
            // Commit only a complete, validated result; a failed refresh never removes the previous review.
            store.Update(s => s.Reviews[id] = review);
            Progress("调查与评析已完成并保存", "completed");
            return review;
        }
        catch (Exception ex)
        {
            Progress(ex is OperationCanceledException ? "调查已取消，已有评析仍保留" : "调查失败，已有评析仍保留", "failed", $"{ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally { gate.Release(); }
    }

    public async Task<List<RenameItem>> Rename(IEnumerable<string> names, CancellationToken ct)
    {
        var list = names.ToArray();
        var answer = await Send(JsonSerializer.Serialize(list), "将文件名规范为 剧名 S01E01.ext，保留扩展名和原语言，不确定时保持原名。只返回JSON数组：[{\"old\":\"原名\",\"new\":\"新名\"}]。不要生成目录。", ct);
        var result = JsonSerializer.Deserialize<List<RenameItem>>(CleanJson(answer), StateStore.Json) ?? [];
        return result.Where(x => list.Contains(x.Old) && x.New == Validation.Segment(x.New)).ToList();
    }

    public static string CleanJson(string text)
    {
        var start = text.IndexOfAny(['{', '[']);
        var end = Math.Max(text.LastIndexOf('}'), text.LastIndexOf(']'));
        return start >= 0 && end >= start ? text[start..(end + 1)] : text;
    }
}
