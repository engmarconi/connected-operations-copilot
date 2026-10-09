using ConnectedOps.Domain;

namespace ConnectedOps.Application;

public interface IOperationsStore
{
    IReadOnlyList<ConnectedAsset> GetAssets();
    IReadOnlyList<Incident> GetIncidents();
    ConnectedAsset? GetAsset(Guid assetId);
    Incident? GetIncident(Guid incidentId);
    IReadOnlyList<TelemetryEvent> GetEvents(Guid assetId);
    Incident CreateOfflineIncident(Guid assetId, DateTimeOffset occurredAt);
    void Reset(DateTimeOffset occurredAt);
}

public interface IDiagnosticService
{
    Task<(DiagnosticAnalysis Analysis, ProposedAction Action)> AnalyzeAsync(
        Incident incident,
        ConnectedAsset asset,
        IReadOnlyList<TelemetryEvent> events,
        CancellationToken cancellationToken);
}

public interface IRunbookRetriever
{
    Task<IReadOnlyList<RunbookChunk>> RetrieveAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken);
}

public sealed record DashboardSummary(
    int TotalAssets,
    int HealthyAssets,
    int OfflineAssets,
    int OpenIncidents,
    int DiagnosedIncidents);

public sealed record DashboardSnapshot(
    DashboardSummary Summary,
    IReadOnlyList<ConnectedAsset> Assets,
    IReadOnlyList<Incident> Incidents,
    DateTimeOffset GeneratedAt);

public sealed class OperationsService(
    IOperationsStore store,
    IDiagnosticService diagnosticService,
    TimeProvider timeProvider)
{
    public DashboardSnapshot GetDashboard()
    {
        var assets = store.GetAssets();
        var incidents = store.GetIncidents()
            .OrderByDescending(incident => incident.OpenedAt)
            .ToArray();

        return new DashboardSnapshot(
            new DashboardSummary(
                assets.Count,
                assets.Count(asset => asset.Health is HealthState.Healthy),
                assets.Count(asset => asset.Health is HealthState.Offline),
                incidents.Count(incident => incident.Status is IncidentStatus.Open),
                incidents.Count(incident => incident.Diagnosis is not null)),
            assets,
            incidents,
            timeProvider.GetUtcNow());
    }

    public async Task<Incident> RunOfflineScenarioAsync(
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var asset = store.GetAsset(assetId)
            ?? throw new KeyNotFoundException($"Asset '{assetId}' was not found.");

        var incident = store.CreateOfflineIncident(assetId, timeProvider.GetUtcNow());
        var events = store.GetEvents(assetId);
        var (analysis, action) = await diagnosticService.AnalyzeAsync(
            incident,
            asset,
            events,
            cancellationToken);

        incident.AddDiagnosis(analysis, action);
        return incident;
    }

    public Incident ApproveNotification(Guid incidentId, string approvedBy)
    {
        var incident = store.GetIncident(incidentId)
            ?? throw new KeyNotFoundException($"Incident '{incidentId}' was not found.");

        incident.ApproveAction(approvedBy, timeProvider.GetUtcNow());
        return incident;
    }

    public DashboardSnapshot Reset()
    {
        store.Reset(timeProvider.GetUtcNow());
        return GetDashboard();
    }
}
