using System.Text.Json;
using ConnectedOps.Application;
using ConnectedOps.Domain;
using ConnectedOps.Infrastructure;

namespace ConnectedOps.Tests;

public sealed class OperationsToolTests
{
    private static readonly Guid VehicleId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid PeerVehicleId =
        Guid.Parse("55555555-5555-5555-5555-555555555555");

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
    public async Task Fleet_comparison_tool_returns_bounded_peer_health_without_mutation()
    {
        var (store, service) = CreateService();
        store.CreateOfflineIncident(VehicleId, DateTimeOffset.UtcNow);
        var healthyBefore = store.GetAssets()
            .Count(asset => asset.Health is HealthState.Healthy);

        var result = await service.ExecuteAsync(
            "fleet-comparison",
            JsonSerializer.SerializeToElement(new
            {
                assetId = VehicleId,
                limit = 3
            }),
            "operator-test",
            CancellationToken.None);

        Assert.Equal("fleet-comparison", result.ToolName);
        Assert.Equal(VehicleId, result.Output.GetProperty("asset").GetProperty("id").GetGuid());
        Assert.Equal(
            HealthState.Offline.ToString(),
            result.Output.GetProperty("asset").GetProperty("health").GetString());
        Assert.Equal(3, result.Output.GetProperty("returnedCount").GetInt32());
        Assert.True(result.Output.GetProperty("hasMore").GetBoolean());
        Assert.Equal(
            1,
            result.Output.GetProperty("fleetSummary").GetProperty("offlineAssets").GetInt32());
        Assert.Equal(
            healthyBefore,
            store.GetAssets().Count(asset => asset.Health is HealthState.Healthy));
        Assert.DoesNotContain(
            result.Output.GetProperty("peers").EnumerateArray(),
            peer => peer.GetProperty("id").GetGuid() == VehicleId);

        var audit = Assert.Single(service.GetAuditTrail());
        Assert.True(audit.Succeeded);
        Assert.Contains("Compared asset", audit.Summary);
    }

    [Fact]
    public async Task Incident_history_tool_returns_status_without_diagnosis_payload()
    {
        var (store, service) = CreateService();
        var timeProvider = new StubTimeProvider(
            new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));
        var operations = new OperationsService(
            store,
            new MockDiagnosticService(new LocalRunbookRetriever(), timeProvider),
            timeProvider);
        await operations.RunOfflineScenarioAsync(VehicleId, CancellationToken.None);

        var result = await service.ExecuteAsync(
            "incident-history",
            JsonSerializer.SerializeToElement(new
            {
                assetId = VehicleId,
                limit = 5
            }),
            "operator-test",
            CancellationToken.None);

        Assert.Equal("incident-history", result.ToolName);
        Assert.Equal(1, result.Output.GetProperty("returnedCount").GetInt32());
        var incident = Assert.Single(result.Output.GetProperty("incidents").EnumerateArray());
        Assert.True(incident.GetProperty("hasDiagnosis").GetBoolean());
        Assert.False(incident.GetProperty("actionApproved").GetBoolean());
        Assert.False(incident.TryGetProperty("diagnosis", out _));
        Assert.False(incident.TryGetProperty("summary", out _));

        var peerHistory = await service.ExecuteAsync(
            "incident-history",
            JsonSerializer.SerializeToElement(new
            {
                assetId = PeerVehicleId,
                limit = 5
            }),
            "operator-test",
            CancellationToken.None);
        Assert.Equal(0, peerHistory.Output.GetProperty("returnedCount").GetInt32());
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
    public void Tool_registry_exposes_only_read_only_definitions()
    {
        var (_, service) = CreateService();

        var definitions = service.GetDefinitions();

        Assert.Equal(3, definitions.Count);
        Assert.All(definitions, definition => Assert.True(definition.IsReadOnly));
        Assert.Equal(
            ["fleet-comparison", "incident-history", "recent-asset-telemetry"],
            definitions.Select(definition => definition.Name).ToArray());
        Assert.All(
            definitions,
            definition => Assert.False(
                definition.InputSchema
                    .GetProperty("additionalProperties")
                    .GetBoolean()));
    }

    private static (InMemoryOperationsStore Store, OperationsToolService Service)
        CreateService()
    {
        var timeProvider = TimeProvider.System;
        var store = new InMemoryOperationsStore(timeProvider);
        var auditStore = new InMemoryToolAuditStore();
        IReadOnlyOperationsTool[] tools =
        [
            new RecentAssetTelemetryTool(store),
            new FleetComparisonTool(store),
            new IncidentHistoryTool(store)
        ];
        var service = new OperationsToolService(
            tools,
            auditStore,
            timeProvider);
        return (store, service);
    }

    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
