using Aria2Fast.Web.Infrastructure;
using Aria2Fast.Web.Models;
using Aria2Fast.Web.Services;

namespace Aria2Fast.Web.Endpoints;

public static class SubscriptionEndpoints
{
    public static void MapSubscriptions(this RouteGroupBuilder api)
    {
        api.MapGet("/subscriptions", (StateStore store) => store.Read().Subscriptions);
        api.MapPost("/subscriptions", (Subscription input, StateStore store) =>
        {
            Validation.Subscription(input, store.Read());
            store.Update(s =>
            {
                var previous = s.Subscriptions.FirstOrDefault(x => x.Id == input.Id);
                if (previous != null) { input.History = previous.History; input.Initialized = previous.Initialized; input.LastChecked = previous.LastChecked; }
                if (s.Subscriptions.Any(x => x.Id != input.Id && x.Url == input.Url && x.NodeId == input.NodeId)) throw new ArgumentException("此节点已订阅该地址");
                s.Subscriptions.RemoveAll(x => x.Id == input.Id); s.Subscriptions.Add(input);
            });
            return input;
        });
        api.MapDelete("/subscriptions/{id}", (string id, StateStore store) => { store.Update(s => s.Subscriptions.RemoveAll(x => x.Id == id)); return Results.Ok(); });
        api.MapPost("/subscriptions/check", (CheckRequest input, SubscriptionService service, CancellationToken ct) => service.Check(input.Id, ct));
        api.MapPost("/subscriptions/{id}/redownload", (string id, SubscriptionService service, CancellationToken ct) => service.Check(id, ct, redownload: true));
        api.MapPost("/subscriptions/preview", async (PreviewRequest input, FeedService feeds, CancellationToken ct) =>
        {
            var feed = await feeds.Fetch(input.Url, ct);
            return new { feed.Title, items = feed.Items.Select(x => new { item = x, matches = Validation.Matches(x.Title, input.Filter ?? "", input.IsFilterRegex)
                && (string.IsNullOrWhiteSpace(input.ExcludeFilter) || !Validation.Matches(x.Title, input.ExcludeFilter, input.IsExcludeFilterRegex ?? input.IsFilterRegex)) }) };
        });
    }
    public sealed record CheckRequest(string? Id);
    public sealed record PreviewRequest(string Url, string? Filter, bool IsFilterRegex, string? ExcludeFilter = null, bool? IsExcludeFilterRegex = null);
}
