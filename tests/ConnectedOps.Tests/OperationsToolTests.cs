using System.Text.Json;
using ConnectedOps.Application;
using ConnectedOps.Infrastructure;

namespace ConnectedOps.Tests;

public sealed class OperationsToolTests
{
    private static readonly Guid VehicleId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Recent_telemetry_tool_is_limited_read_only_and_audited()
    {
        var (store, service) = CreateService();
        store.CreateOfflineIncident(VehicleId, DateTimeOffset.UtcNow);
        var incidentCount = store.GetIncidents().Count;
        var eventCount = store.GetEvents(VehicleId).Count;

        var result = await service.ExecuteAsync(
            "recent-asset-telemetry",
            JsonSerializer.SerializeToElement(new
            {
                assetId = VehicleId,
                limit = 2
            }),
            "operator-test",
            CancellationToken.None);

        Assert.Equal("recent-asset-telemetry", result.ToolName);
        Assert.Equal(2, result.Output.GetProperty("returnedCount").GetInt32());
        Assert.Equal(2, result.Output.GetProperty("events").GetArrayLength());
        Assert.True(result.Output.GetProperty("hasMore").GetBoolean());
        Assert.Equal(incidentCount, store.GetIncidents().Count);
        Assert.Equal(eventCount, store.GetEvents(VehicleId).Count);

        var audit = Assert.Single(service.GetAuditTrail());
        Assert.True(audit.Succeeded);
        Assert.Equal("operator-test", audit.Caller);
        Assert.Contains("Returned 2 telemetry", audit.Summary);
    }

    [Fact]
    public async Task Invalid_tool_arguments_fail_safely_and_are_audited()
    {
        var (_, service) = CreateService();

        var exception = await Assert.ThrowsAsync<OperationsToolException>(
            () => service.ExecuteAsync(
                "recent-asset-telemetry",
                JsonSerializer.SerializeToElement(new
                {
                    assetId = VehicleId,
                    limit = 200,
                    writeMode = true
                }),
                "operator-test",
                CancellationToken.None));

        Assert.Equal(OperationsToolErrorKind.InvalidArguments, exception.Kind);
        Assert.Contains("Unknown tool argument", exception.Message);
        var audit = Assert.Single(service.GetAuditTrail());
        Assert.False(audit.Succeeded);
    }

    [Fact]
    public void Tool_registry_exposes_only_read_only_definition()
    {
        var (_, service) = CreateService();

        var definition = Assert.Single(service.GetDefinitions());

        Assert.Equal("recent-asset-telemetry", definition.Name);
        Assert.True(definition.IsReadOnly);
        Assert.Equal(2_000, definition.TimeoutMilliseconds);
        Assert.False(
            definition.InputSchema
                .GetProperty("additionalProperties")
                .GetBoolean());
    }

    private static (InMemoryOperationsStore Store, OperationsToolService Service)
        CreateService()
    {
        var timeProvider = TimeProvider.System;
        var store = new InMemoryOperationsStore(timeProvider);
        var auditStore = new InMemoryToolAuditStore();
        IReadOnlyOperationsTool[] tools =
        [
            new RecentAssetTelemetryTool(store)
        ];
        var service = new OperationsToolService(
            tools,
            auditStore,
            timeProvider);
        return (store, service);
    }
}
