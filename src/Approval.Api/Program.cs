using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Approval.Api;

var builder = WebApplication.CreateBuilder(args);
var developmentAuth = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
var authority = builder.Configuration["Authentication:Authority"];
var audience = builder.Configuration["Authentication:Audience"];
if (!developmentAuth)
{
    if (!Uri.TryCreate(authority, UriKind.Absolute, out var issuer) || issuer.Scheme != Uri.UriSchemeHttps)
        throw new InvalidOperationException("Set Authentication:Authority to your HTTPS OIDC issuer.");
    if (string.IsNullOrWhiteSpace(audience))
        throw new InvalidOperationException("Set Authentication:Audience.");
}
builder.Services.AddProblemDetails();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters.NameClaimType = "sub";
    options.TokenValidationParameters.RoleClaimType = "role";
    options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
    // Development signing keys are supplied by dotnet user-jwts, not by the API.
    if (!developmentAuth)
    {
        options.Authority = authority;
        options.Audience = audience;
        options.RequireHttpsMetadata = true;
    }
});
builder.Services.AddAuthorization(options =>
{
    void Member(Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder policy) => policy
        .RequireAuthenticatedUser()
        .RequireAssertion(context => !string.IsNullOrWhiteSpace(context.User.FindFirstValue("sub"))
            && !string.IsNullOrWhiteSpace(context.User.FindFirstValue("org")));
    options.AddPolicy("member", Member);
    options.AddPolicy("maker", policy => { Member(policy); policy.RequireRole("Maker"); });
    options.AddPolicy("checker", policy => { Member(policy); policy.RequireRole("Checker"); });
});
builder.Services.AddSingleton(new ApprovalStore(builder.Configuration.GetConnectionString("Approvals") ?? "Data Source=approvals.db;Default Timeout=10"));
builder.Services.AddSingleton<INotificationSink, LoggingNotificationSink>();
builder.Services.AddSingleton<OutboxPump>();
builder.Services.AddHostedService<OutboxWorker>();
var app = builder.Build();
app.UseExceptionHandler(error => error.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    if (exception is WorkflowException workflow)
    {
        context.Response.StatusCode = workflow.Status;
        await Results.Problem(statusCode: workflow.Status, title: workflow.Message).ExecuteAsync(context);
    }
    else
    {
        // Do not put SQL, payloads, tokens or stack traces in an HTTP response.
        context.Response.StatusCode = 500;
        await Results.Problem(statusCode: 500, title: "An unexpected error occurred.").ExecuteAsync(context);
    }
}));
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.Services.GetRequiredService<ApprovalStore>().Initialize();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapPost("/requests", (CreateRequest input, ClaimsPrincipal user, ApprovalStore store) =>
{
    if (string.IsNullOrWhiteSpace(input.Title) || input.Title.Trim().Length > 160)
        throw new WorkflowException(400, "Title must contain 1 to 160 characters.");
    var request = store.Submit(Actor.From(user), input.Title.Trim());
    return Results.Created($"/requests/{request.Id}", request);
}).RequireAuthorization("maker");
app.MapGet("/requests/{id:guid}", (Guid id, ClaimsPrincipal user, ApprovalStore store) =>
    Results.Ok(store.Get(id, Actor.From(user)))).RequireAuthorization("member");
app.MapPost("/requests/{id:guid}/decisions", (Guid id, DecisionInput input, HttpContext context, ApprovalStore store) =>
{
    var key = context.Request.Headers["Idempotency-Key"].ToString();
    if (key.Length is < 1 or > 128 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
        throw new WorkflowException(400, "Idempotency-Key must be 1 to 128 ASCII letters, digits, hyphens or underscores.");
    if (input.ExpectedVersion < 1 || (input.Decision != "Approved" && input.Decision != "Rejected"))
        throw new WorkflowException(400, "Use a positive expectedVersion and decision Approved or Rejected.");
    return Results.Ok(store.Decide(id, Actor.From(context.User), input, key));
}).RequireAuthorization("checker");
app.Run();
public partial class Program { }
