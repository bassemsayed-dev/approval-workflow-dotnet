using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using Approval.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Approval.Tests;

public sealed class ApiFixture : WebApplicationFactory<Program>
{
    private readonly string database = Path.Combine(Path.GetTempPath(), $"approvals-{Guid.NewGuid()}.db");
    private readonly SymmetricSecurityKey key = new(RandomNumberGenerator.GetBytes(32));
    public ApprovalStore Store => Services.GetRequiredService<ApprovalStore>();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Outbox:Disabled"] = "true" }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ApprovalStore>();
            services.AddSingleton(new ApprovalStore($"Data Source={database};Default Timeout=10"));
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = "approval-tests",
                    ValidAudience = "approval-api",
                    IssuerSigningKey = key,
                    ValidateIssuerSigningKey = true,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                    ClockSkew = TimeSpan.Zero
                };
            });
        });
    }
    public HttpClient As(string subject, string role = "Maker", string? org = "demo", bool expired = false)
    {
        var claims = new List<Claim> { new("sub", subject), new("role", role) };
        if (org is not null) claims.Add(new("org", org));
        var jwt = new JwtSecurityToken("approval-tests", "approval-api", claims,
            DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(expired ? -1 : 10),
            new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        return client;
    }
    public async Task<ApprovalRequest> Submit(HttpClient maker, string title = "Enable a feature flag")
    {
        using var response = await maker.PostAsJsonAsync("/requests", new CreateRequest(title));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApprovalRequest>())!;
    }
    public static async Task<HttpResponseMessage> Decide(HttpClient checker, string id, string key = "decision-1", int version = 1, string decision = "Approved")
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"/requests/{id}/decisions");
        message.Headers.Add("Idempotency-Key", key);
        message.Content = JsonContent.Create(new DecisionInput(version, decision));
        return await checker.SendAsync(message);
    }
    public long Count(string table)
    {
        // Only test-owned constant table names call this helper.
        using var connection = Store.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT count(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(database + suffix);
        }
    }
}

public class ApiTests
{
    [Fact]
    public async Task Anonymous_requests_require_authentication()
    {
        using var app = new ApiFixture(); using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/requests", new CreateRequest("x"))).StatusCode);
    }
    [Fact]
    public async Task Expired_token_is_rejected()
    {
        using var app = new ApiFixture(); using var client = app.As("maker", expired: true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/requests", new CreateRequest("x"))).StatusCode);
    }
    [Fact]
    public async Task Invalid_token_is_rejected()
    {
        using var app = new ApiFixture(); using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", "invalid-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/requests", new CreateRequest("x"))).StatusCode);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Organization_claim_is_required(string? organization)
    {
        using var app = new ApiFixture(); using var client = app.As("maker", org: organization);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/requests", new CreateRequest("x"))).StatusCode);
    }
    [Fact]
    public async Task Checker_cannot_submit()
    {
        using var app = new ApiFixture(); using var client = app.As("checker", "Checker");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/requests", new CreateRequest("x"))).StatusCode);
    }
    [Fact]
    public async Task Maker_cannot_decide_without_checker_role()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); var request = await app.Submit(maker);
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiFixture.Decide(maker, request.Id)).StatusCode);
    }
    [Fact]
    public async Task Self_approval_is_denied_even_with_checker_role()
    {
        using var app = new ApiFixture(); using var maker = app.As("same-person"); var request = await app.Submit(maker);
        using var checker = app.As("same-person", "Checker");
        Assert.Equal(HttpStatusCode.Forbidden, (await ApiFixture.Decide(checker, request.Id)).StatusCode);
        Assert.Equal("Submitted", app.Store.Get(Guid.Parse(request.Id), new("same-person", "demo")).State);
        Assert.Equal(0, app.Count("Outbox"));
    }
    [Fact]
    public async Task Other_organization_cannot_read_or_decide()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); var request = await app.Submit(maker);
        using var outsider = app.As("checker", "Checker", "other-org");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/requests/{request.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ApiFixture.Decide(outsider, request.Id)).StatusCode);
    }
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Invalid_title_is_rejected(string title)
    {
        using var app = new ApiFixture(); using var maker = app.As("maker");
        Assert.Equal(HttpStatusCode.BadRequest, (await maker.PostAsJsonAsync("/requests", new CreateRequest(title))).StatusCode);
    }
    [Fact]
    public async Task Submit_stores_title_as_data_and_audits_once()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker");
        var request = await app.Submit(maker, "O'Brien; DROP TABLE Requests;");
        Assert.Equal("O'Brien; DROP TABLE Requests;", request.Title); Assert.Equal(1, request.Version);
        Assert.Equal(1, app.Count("Requests")); Assert.Equal(1, app.Count("Audit"));
    }
    [Theory]
    [InlineData("Approved")]
    [InlineData("Rejected")]
    public async Task Decision_commits_state_audit_and_outbox_together(string state)
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); using var checker = app.As("checker", "Checker");
        var request = await app.Submit(maker);
        var response = await ApiFixture.Decide(checker, request.Id, decision: state);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<DecisionResult>())!;
        Assert.Equal(state, result.State); Assert.Equal(2, result.Version);
        Assert.Equal(2, app.Count("Audit")); Assert.Equal(1, app.Count("Outbox")); Assert.Equal(1, app.Count("Decisions"));
    }
    [Fact]
    public async Task Stale_version_has_no_side_effects()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); using var checker = app.As("checker", "Checker");
        var request = await app.Submit(maker);
        Assert.Equal(HttpStatusCode.Conflict, (await ApiFixture.Decide(checker, request.Id, version: 9)).StatusCode);
        Assert.Equal(1, app.Count("Audit")); Assert.Equal(0, app.Count("Outbox"));
    }
    [Fact]
    public async Task Retry_replays_same_result_without_duplicate_effects()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); using var checker = app.As("checker", "Checker");
        var request = await app.Submit(maker);
        var first = await ApiFixture.Decide(checker, request.Id); var second = await ApiFixture.Decide(checker, request.Id);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); Assert.Equal(1, app.Count("Outbox")); Assert.Equal(2, app.Count("Audit"));
    }
    [Fact]
    public async Task Reusing_key_with_changed_input_is_a_conflict()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); using var checker = app.As("checker", "Checker");
        var request = await app.Submit(maker); await ApiFixture.Decide(checker, request.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await ApiFixture.Decide(checker, request.Id, decision: "Rejected")).StatusCode);
    }
    [Fact]
    public async Task Terminal_request_cannot_be_decided_again()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); using var checker = app.As("checker", "Checker");
        var request = await app.Submit(maker); await ApiFixture.Decide(checker, request.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await ApiFixture.Decide(checker, request.Id, key: "new-key", version: 2)).StatusCode);
    }
    [Fact]
    public async Task Racing_checkers_commit_exactly_one_decision()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker");
        using var first = app.As("checker-a", "Checker"); using var second = app.As("checker-b", "Checker");
        var request = await app.Submit(maker);
        var results = await Task.WhenAll(Task.Run(() => ApiFixture.Decide(first, request.Id)), Task.Run(() => ApiFixture.Decide(second, request.Id)));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, app.Count("Decisions")); Assert.Equal(1, app.Count("Outbox")); Assert.Equal(2, app.Count("Audit"));
    }
    [Fact]
    public async Task Outbox_failure_rolls_back_decision_and_audit()
    {
        using var app = new ApiFixture(); using var maker = app.As("maker"); using var checker = app.As("checker", "Checker");
        var request = await app.Submit(maker);
        using (var connection = app.Store.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER fail_outbox BEFORE INSERT ON Outbox BEGIN SELECT RAISE(ABORT,'test fault'); END;";
            command.ExecuteNonQuery();
        }
        var response = await ApiFixture.Decide(checker, request.Id);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("test fault", await response.Content.ReadAsStringAsync());
        Assert.Equal("Submitted", app.Store.Get(Guid.Parse(request.Id), new("maker", "demo")).State);
        Assert.Equal(1, app.Count("Audit")); Assert.Equal(0, app.Count("Decisions")); Assert.Equal(0, app.Count("Outbox"));
    }
}
