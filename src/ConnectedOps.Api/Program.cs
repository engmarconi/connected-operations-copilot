using System.Text.Json.Serialization;
using ConnectedOps.Api;
using ConnectedOps.Application;
using ConnectedOps.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.AddConnectedOperationsObservability();
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

operations.MapGet("/operations/dashboard", (OperationsService service) =>
    Results.Ok(service.GetDashboard()));

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
    });

operations.MapPost("/simulation/reset", (OperationsService service) =>
    Results.Ok(service.Reset()));

operations.MapGet("/tools", (OperationsToolService service) =>
    Results.Ok(new { tools = service.GetDefinitions() }));

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
    });

operations.MapPost(
    "/tools/{toolName}",
    async (
        string toolName,
        ToolExecutionRequest request,
        OperationsToolService service,
        CancellationToken cancellationToken) =>
    {
        try
        {
            var result = await service.ExecuteAsync(
                toolName,
                request.Arguments,
                "operator-api",
                cancellationToken);
            return Results.Ok(result);
        }
        catch (OperationsToolException exception)
        {
            return ToolError(exception);
        }
    });

operations.MapPost(
    "/incidents/{incidentId:guid}/approve-notification",
    (Guid incidentId, ApproveActionRequest request, OperationsService service) =>
    {
        try
        {
            var incident = service.ApproveNotification(
                incidentId,
                string.IsNullOrWhiteSpace(request.ApprovedBy)
                    ? "Demo operator"
                    : request.ApprovedBy.Trim());
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
    });

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

public sealed record ApproveActionRequest(string ApprovedBy);
public sealed record ToolExecutionRequest(
    System.Text.Json.JsonElement Arguments);
