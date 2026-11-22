using System.Net;
using System.Net.Http.Json;
using Approval.Api;

namespace Approval.Tests;

public class ValidationTests
{
    [Fact]
    public async Task Too_long_title_is_bad_request()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker");
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/requests", new CreateRequest(new string('x', 161)))).StatusCode);
    }
    [Theory]
    [InlineData(0, "Approved")]
    [InlineData(1, "Deleted")]
    public async Task Invalid_decision_has_no_effect(int version, string decision)
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); using var checker = app.As("checker", "Checker");
        var request = await app.Submit(maker);
        Assert.Equal(HttpStatusCode.BadRequest, (await ApiFixture.Decide(checker, request.Id, version: version, decision: decision)).StatusCode);
        Assert.Equal(0, app.Count("Outbox"));
    }
    [Fact]
    public async Task Missing_idempotency_key_is_bad_request()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); using var checker = app.As("checker", "Checker");
        var request = await app.Submit(maker);
        Assert.Equal(HttpStatusCode.BadRequest, (await checker.PostAsJsonAsync($"/requests/{request.Id}/decisions", new DecisionInput(1, "Approved"))).StatusCode);
    }
    [Fact]
    public async Task Reusing_key_for_another_resource_is_a_conflict()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); using var checker = app.As("checker", "Checker");
        var first = await app.Submit(maker); var second = await app.Submit(maker);
        await ApiFixture.Decide(checker, first.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await ApiFixture.Decide(checker, second.Id)).StatusCode);
    }
    [Fact]
    public async Task Concurrent_identical_retries_return_same_result()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); using var checker = app.As("checker", "Checker");
        var request = await app.Submit(maker);
        var results = await Task.WhenAll(Task.Run(() => ApiFixture.Decide(checker, request.Id)), Task.Run(() => ApiFixture.Decide(checker, request.Id)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(await results[0].Content.ReadAsStringAsync(), await results[1].Content.ReadAsStringAsync());
        Assert.Equal(1, app.Count("Outbox")); Assert.Equal(1, app.Count("Decisions"));
    }
}
