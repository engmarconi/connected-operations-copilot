using System.Diagnostics;
using ConnectedOps.Domain;
using Microsoft.Extensions.Logging;

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

public sealed class DiagnosticProviderException(
    string message,
    Exception? innerException = null) : Exception(message, innerException);

public interface IRunbookRetriever
{
    string ProviderId { get; }

    Task<IReadOnlyList<RunbookChunk>> RetrieveAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken);
}

public enum EmbeddingPurpose
{
    Document,
    Query
}

public interface ITextEmbeddingService
{
    int Dimensions { get; }
    string ModelId { get; }

    Task<float[]> EmbedAsync(
        string text,
        EmbeddingPurpose purpose,
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
    TimeProvider timeProvider,
    ILogger<OperationsService>? logger = null)
{
    private readonly SemaphoreSlim _scenarioGate = new(1, 1);

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
        await _scenarioGate.WaitAsync(cancellationToken);
        using var activity = OperationsTelemetry.ActivitySource.StartActivity(
            "operations.offline_scenario");
        activity?.SetTag("asset.id", assetId.ToString());
        OperationsTelemetry.OfflineScenarios.Add(1);

        try
        {
            var asset = store.GetAsset(assetId)
                ?? throw new KeyNotFoundException($"Asset '{assetId}' was not found.");
            activity?.SetTag("asset.name", asset.Name);
            activity?.SetTag("asset.type", asset.Type.ToString());

            var incident = store.CreateOfflineIncident(assetId, timeProvider.GetUtcNow());
            activity?.SetTag("incident.id", incident.Id.ToString());
            if (incident.Diagnosis is not null)
            {
                activity?.SetTag("diagnosis.cached", true);
                logger?.LogInformation(
                    "Returning cached diagnosis for asset {AssetId} incident {IncidentId}",
                    assetId,
                    incident.Id);
                return incident;
            }

            var events = store.GetEvents(assetId);
            activity?.SetTag("telemetry.count", events.Count);
            try
            {
                var (analysis, action) = await diagnosticService.AnalyzeAsync(
                    incident,
                    asset,
                    events,
                    cancellationToken);

                incident.AddDiagnosis(analysis, action);
                OperationsTelemetry.DiagnosesCompleted.Add(
                    1,
                    new KeyValuePair<string, object?>("provider", analysis.Provider));
                activity?.SetTag("diagnosis.provider", analysis.Provider);
                activity?.SetTag("diagnosis.confidence", analysis.Confidence);
                activity?.SetTag("diagnosis.cached", false);
                logger?.LogInformation(
                    "Completed diagnosis for asset {AssetId} incident {IncidentId} with provider {Provider} and confidence {Confidence}",
                    assetId,
                    incident.Id,
                    analysis.Provider,
                    analysis.Confidence);
                return incident;
            }
            catch (Exception exception)
            {
                OperationsTelemetry.DiagnosesFailed.Add(
                    1,
                    new KeyValuePair<string, object?>("error.type", exception.GetType().Name));
                activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                logger?.LogError(
                    exception,
                    "Diagnosis failed safely for asset {AssetId} incident {IncidentId}",
                    assetId,
                    incident.Id);
                throw;
            }
        }
        finally
        {
            _scenarioGate.Release();
        }
    }

    public Incident ApproveNotification(Guid incidentId, string approvedBy)
    {
        using var activity = OperationsTelemetry.ActivitySource.StartActivity(
            "operations.approve_notification");
        activity?.SetTag("incident.id", incidentId.ToString());
        activity?.SetTag("approval.actor_present", !string.IsNullOrWhiteSpace(approvedBy));

        var incident = store.GetIncident(incidentId)
            ?? throw new KeyNotFoundException($"Incident '{incidentId}' was not found.");

        incident.ApproveAction(approvedBy, timeProvider.GetUtcNow());
        OperationsTelemetry.Approvals.Add(1);
        logger?.LogInformation(
            "Approved notification for incident {IncidentId}",
            incidentId);
        return incident;
    }

    public DashboardSnapshot Reset()
    {
        using var activity = OperationsTelemetry.ActivitySource.StartActivity(
            "operations.reset");
        store.Reset(timeProvider.GetUtcNow());
        logger?.LogInformation("Reset synthetic operations state");
        return GetDashboard();
    }
}
