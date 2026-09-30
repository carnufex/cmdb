using System.Net;
using System.Net.Http.Json;
using Cmdb.Api.Features.Changelog;
using Cmdb.Database;
using Cmdb.Database.Changelog;

namespace Cmdb.Api.IntegrationTests.Features.Changelog;

/// <summary>The user-facing change log (#82): reviewed posts from the repository, unread per user, and for agents.</summary>
public sealed class ChangelogTests(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Shows_published_posts_and_what_the_user_has_not_read()
    {
        using var client = factory.CreateAuthenticatedClient($"reader-{Guid.NewGuid():N}");
        var published = ChangelogSync.Embedded().Where(e => e.Published).ToList();

        var log = (await client.GetFromJsonAsync<ChangelogResponse>("/api/changelog", Ct))!;
        log.Posts.Select(p => p.Key).ShouldBe(published.Select(e => e.Key), ignoreOrder: true);
        log.Posts.Select(p => p.Date).ShouldBeInOrder(SortDirection.Descending);
        log.Unread.ShouldBe(published.Count);

        (await client.PostAsync("/api/changelog/read", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<ChangelogResponse>("/api/changelog", Ct))!.Unread.ShouldBe(0);
    }

    [Fact]
    public async Task Sync_publishes_unpublishes_and_removes_by_key()
    {
        await using var db = await factory.NewDatabaseAsync();
        await CmdbDatabase.MigrateAsync(db, Ct);
        await using var context = CmdbDatabase.CreateContext(db);
        ChangelogSync.Entry Post(string key, bool published) =>
            new(key, "sha-1", new DateOnly(2026, 9, 30), "nytt", key, "Text.", null, [1], published);

        await ChangelogSync.SyncAsync(context, [Post("a", true), Post("b", false)], Ct);
        var first = context.ChangelogEntries.Single(e => e.Key == "a").PublishedAt;
        first.ShouldNotBeNull();
        context.ChangelogEntries.Single(e => e.Key == "b").PublishedAt.ShouldBeNull();

        await ChangelogSync.SyncAsync(context, [Post("a", true), Post("b", true)], Ct);
        context.ChangelogEntries.Single(e => e.Key == "a").PublishedAt.ShouldBe(first, "republishing keeps the first time");
        context.ChangelogEntries.Single(e => e.Key == "b").PublishedAt.ShouldNotBeNull();

        await ChangelogSync.SyncAsync(context, [Post("b", false)], Ct);
        context.ChangelogEntries.Select(e => e.Key).ShouldBe(["b"]);
        context.ChangelogEntries.Single().PublishedAt.ShouldBeNull();
    }
}
