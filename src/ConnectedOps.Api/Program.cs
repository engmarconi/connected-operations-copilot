using System.Security.Claims;
using System.Text.Json.Serialization;
using ConnectedOps.Api;
using ConnectedOps.Application;
using ConnectedOps.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddConnectedOperationsObservability();
builder.AddConnectedOperationsAuth();
builder.Services.AddConnectedOperations(builder.Configuration);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:43123",
                "http://127.0.0.1:43123",
                "http://localhost:3000")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = OperationsTelemetry.ServiceName,
    timestamp = DateTimeOffset.UtcNow,
    telemetry = new
    {
        activitySource = OperationsTelemetry.ActivitySourceName,
        meter = OperationsTelemetry.MeterName
    }
}));

var operations = app.MapGroup("/api");

operations.MapPost(
    "/auth/login",
    (LoginRequest request, IDemoUserDirectory users, IAuthTokenService tokens) =>
    {
        var user = users.Authenticate(request.Username, request.Password);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        return Results.Ok(tokens.IssueToken(user));
    });

operations.MapGet(
    "/auth/me",
    (ClaimsPrincipal principal, IDemoUserDirectory users) =>
    {
        var username = principal.Identity?.Name;
        var user = username is null ? null : users.FindByUsername(username);
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(user);
    }).RequireAuthorization();

operations.MapGet(
    "/auth/demo-users",
    () => Results.Ok(new
    {
        users = DemoUserDirectory.ListUsers().Select(user => new
        {
            user.Username,
            user.DisplayName,
            user.Roles
        })
    }));

operations.MapGet(
    "/operations/dashboard",
    (OperationsService service) => Results.Ok(service.GetDashboard()))
    .RequireAuthorization();

operations.MapPost(
    "/simulation/offline/{assetId:guid}",
    async (Guid assetId, OperationsService service, CancellationToken cancellationToken) =>
    {
        try
        {
            var incident = await service.RunOfflineScenarioAsync(assetId, cancellationToken);
            return Results.Ok(incident);
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { error = exception.Message });
        }
        catch (DiagnosticProviderException exception)
        {
            return Results.Problem(
                title: "Diagnostic provider failed safely",
                detail: exception.Message,
                statusCode: StatusCodes.Status502BadGateway);
        }
    }).RequireAuthorization(AuthPolicies.CanOperate);

operations.MapPost(
    "/simulation/reset",
    (OperationsService service) => Results.Ok(service.Reset()))
    .RequireAuthorization(AuthPolicies.CanOperate);

operations.MapGet(
    "/tools",
    (OperationsToolService service) => Results.Ok(new { tools = service.GetDefinitions() }))
    .RequireAuthorization();

operations.MapGet(
    "/tools/audit",
    (int? limit, OperationsToolService service) =>
    {
        try
        {
            return Results.Ok(new
            {
                records = service.GetAuditTrail(limit ?? 20)
            });
        }
        catch (OperationsToolException exception)
        {
            return ToolError(exception);
        }
    }).RequireAuthorization();

operations.MapPost(
    "/tools/{toolName}",
    async (
        string toolName,
        ToolExecutionRequest request,
        ClaimsPrincipal principal,
        OperationsToolService service,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var caller = principal.Identity?.Name is { Length: > 0 } username
                ? $"operator-api/{username}"
                : "operator-api";
            var result = await service.ExecuteAsync(
                toolName,
                request.Arguments,
                caller,
                cancellationToken);
            return Results.Ok(result);
        }
        catch (OperationsToolException exception)
        {
            return ToolError(exception);
        }
    }).RequireAuthorization(AuthPolicies.CanOperate);

operations.MapPost(
    "/incidents/{incidentId:guid}/approve-notification",
    (Guid incidentId, ClaimsPrincipal principal, OperationsService service) =>
    {
        try
        {
            var approvedBy = principal.FindFirstValue("name")
                ?? principal.Identity?.Name
                ?? "Unknown approver";
            var incident = service.ApproveNotification(incidentId, approvedBy);
            return Results.Ok(incident);
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }).RequireAuthorization(AuthPolicies.CanApprove);

static IResult ToolError(OperationsToolException exception)
{
    var statusCode = exception.Kind switch
    {
        OperationsToolErrorKind.InvalidArguments => StatusCodes.Status400BadRequest,
        OperationsToolErrorKind.NotFound => StatusCodes.Status404NotFound,
        OperationsToolErrorKind.TimedOut => StatusCodes.Status504GatewayTimeout,
        _ => StatusCodes.Status502BadGateway
    };
    return Results.Problem(
        title: "Read-only tool execution failed safely",
        detail: exception.Message,
        statusCode: statusCode);
}

app.Run();

public sealed record ToolExecutionRequest(
    System.Text.Json.JsonElement Arguments);

public partial class Program;
