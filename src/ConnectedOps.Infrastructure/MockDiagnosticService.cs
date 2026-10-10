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
        var citationIds = citations
            .Select(citation => citation.DocumentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hardwareLed = citationIds.Contains("RB-HW-002")
            && !citationIds.Contains("RB-CONN-001");

        DiagnosticAnalysis analysis;
        ProposedAction action;
        if (hardwareLed)
        {
            analysis = new DiagnosticAnalysis(
                $"{asset.Name} shows physical-device evidence consistent with a power, enclosure, cable, or sensor fault. "
                + "A network outage is not established by the available telemetry; keep inspection bounded and approval-gated.",
                [
                    "Probable power, enclosure, cable, or sensor fault",
                    "Local connector damage or enclosure integrity issue"
                ],
                [
                    "Isolate power before opening an enclosure.",
                    "Record visible damage and connector state without changing configuration.",
                    "Escalate only if inspection confirms a hardware fault."
                ],
                citations,
                0.74m,
                $"mock-generator/{runbookRetriever.ProviderId}",
                timeProvider.GetUtcNow());
            action = new ProposedAction
            {
                Id = Guid.NewGuid(),
                Kind = "notify-operator",
                Description = $"Notify operations to inspect {asset.Name} using the hardware inspection checklist."
            };
        }
        else
        {
            var confidence = hasTimeout && hasOfflineEvent ? 0.92m : 0.58m;
            analysis = new DiagnosticAnalysis(
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
            action = new ProposedAction
            {
                Id = Guid.NewGuid(),
                Kind = "notify-operator",
                Description = $"Notify operations to contact the driver assigned to {asset.Name} and run the connectivity checklist."
            };
        }

        return (analysis, action);
    }
}
