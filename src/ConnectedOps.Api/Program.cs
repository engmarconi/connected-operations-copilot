using System.Text.Json.Serialization;
using ConnectedOps.Application;
using ConnectedOps.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddConnectedOperations();
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
    service = "connected-operations-api",
    timestamp = DateTimeOffset.UtcNow
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
    });

operations.MapPost("/simulation/reset", (OperationsService service) =>
    Results.Ok(service.Reset()));

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

app.Run();

public sealed record ApproveActionRequest(string ApprovedBy);
