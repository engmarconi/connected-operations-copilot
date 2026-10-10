using ConnectedOps.Application;
using ConnectedOps.Domain;

namespace ConnectedOps.Infrastructure;

public sealed class MockDiagnosticService(
    IRunbookRetriever runbookRetriever,
    TimeProvider timeProvider) : IDiagnosticService
{
    public async Task<(DiagnosticAnalysis Analysis, ProposedAction Action)> AnalyzeAsync(
        Incident incident,
        ConnectedAsset asset,
        IReadOnlyList<TelemetryEvent> events,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hasTimeout = events.Any(item => item.EventType is "request.timeout");
        var hasOfflineEvent = events.Any(item => item.EventType is "asset.offline");
        var confidence = hasTimeout && hasOfflineEvent ? 0.92m : 0.58m;
        var retrievedChunks = await runbookRetriever.RetrieveAsync(
            DiagnosticSafety.BuildRetrievalQuery(incident, asset, events),
            maxResults: 2,
            cancellationToken);
        DiagnosticSafety.EnsureEvidence(retrievedChunks.Count, "Mock");
        var citations = retrievedChunks
            .Select(chunk => new RunbookCitation(
                chunk.DocumentId,
                chunk.Title,
                chunk.Section,
                chunk.Content))
            .ToArray();

        var analysis = new DiagnosticAnalysis(
            $"{asset.Name} stopped reporting after request latency rose across several status updates. "
            + "The evidence is consistent with a lost mobile or upstream network connection; "
            + "no device-side hardware fault is proven by the available telemetry.",
            [
                "Loss of mobile or upstream network connectivity",
                "Expired application session or stalled background process"
            ],
            [
                "Confirm the driver phone has an active data connection.",
                "Ask the driver to reopen the application and verify session status.",
                "Escalate to device inspection only if reporting does not recover."
            ],
            citations,
            confidence,
            $"mock-generator/{runbookRetriever.ProviderId}",
            timeProvider.GetUtcNow());

        var action = new ProposedAction
        {
            Id = Guid.NewGuid(),
            Kind = "notify-operator",
            Description = $"Notify operations to contact the driver assigned to {asset.Name} and run the connectivity checklist."
        };

        return (analysis, action);
    }
}
