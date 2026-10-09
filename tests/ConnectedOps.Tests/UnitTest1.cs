using ConnectedOps.Application;
using ConnectedOps.Domain;
using ConnectedOps.Infrastructure;

namespace ConnectedOps.Tests;

public sealed class OperationsServiceTests
{
    private static readonly Guid VehicleId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Dashboard_starts_with_all_assets_healthy()
    {
        var service = CreateService();

        var dashboard = service.GetDashboard();

        Assert.Equal(6, dashboard.Summary.TotalAssets);
        Assert.Equal(6, dashboard.Summary.HealthyAssets);
        Assert.Equal(0, dashboard.Summary.OfflineAssets);
        Assert.Empty(dashboard.Incidents);
    }

    [Fact]
    public async Task Offline_scenario_creates_cited_diagnosis_and_unapproved_action()
    {
        var service = CreateService();

        var incident = await service.RunOfflineScenarioAsync(
            VehicleId,
            CancellationToken.None);

        Assert.Equal("High", incident.Severity);
        Assert.NotNull(incident.Diagnosis);
        Assert.Equal("mock-evidence-provider", incident.Diagnosis.Provider);
        Assert.Equal(2, incident.Diagnosis.Citations.Count);
        Assert.All(
            incident.Diagnosis.Citations,
            citation => Assert.False(string.IsNullOrWhiteSpace(citation.DocumentId)));
        Assert.NotNull(incident.ProposedAction);
        Assert.True(incident.ProposedAction.RequiresApproval);
        Assert.False(incident.ProposedAction.IsApproved);

        var dashboard = service.GetDashboard();
        Assert.Equal(1, dashboard.Summary.OfflineAssets);
        Assert.Equal(1, dashboard.Summary.OpenIncidents);
    }

    [Fact]
    public async Task Notification_is_only_simulated_after_human_approval()
    {
        var service = CreateService();
        var incident = await service.RunOfflineScenarioAsync(
            VehicleId,
            CancellationToken.None);

        var approved = service.ApproveNotification(incident.Id, "Marco");

        Assert.Equal(IncidentStatus.ActionApproved, approved.Status);
        Assert.True(approved.ProposedAction!.IsApproved);
        Assert.Equal("Marco", approved.ProposedAction.ApprovedBy);
        Assert.Contains("simulated", approved.ProposedAction.SimulatedResult);
    }

    private static OperationsService CreateService()
    {
        var timeProvider = new StubTimeProvider(
            new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero));
        var store = new InMemoryOperationsStore(timeProvider);
        var diagnosticService = new MockDiagnosticService(timeProvider);
        return new OperationsService(store, diagnosticService, timeProvider);
    }

    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
