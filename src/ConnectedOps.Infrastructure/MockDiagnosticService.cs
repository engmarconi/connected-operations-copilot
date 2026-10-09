using ConnectedOps.Application;
using ConnectedOps.Domain;

namespace ConnectedOps.Infrastructure;

public sealed class MockDiagnosticService(TimeProvider timeProvider) : IDiagnosticService
{
    public Task<(DiagnosticAnalysis Analysis, ProposedAction Action)> AnalyzeAsync(
        Incident incident,
        ConnectedAsset asset,
        IReadOnlyList<TelemetryEvent> events,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var hasTimeout = events.Any(item => item.EventType is "request.timeout");
        var hasOfflineEvent = events.Any(item => item.EventType is "asset.offline");
        var confidence = hasTimeout && hasOfflineEvent ? 0.92m : 0.58m;

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
            [
                new RunbookCitation(
                    "RB-CONN-001",
                    "Mobile Connectivity Runbook",
                    "2.1 — Rising latency and heartbeat loss",
                    "Repeated latency growth followed by timeouts usually indicates a degraded or lost network path."),
                new RunbookCitation(
                    "RB-APP-003",
                    "Driver Application Recovery",
                    "3.4 — Safe operator response",
                    "Confirm connectivity and application session state before requesting a restart or device inspection.")
            ],
            confidence,
            "mock-evidence-provider",
            timeProvider.GetUtcNow());

        var action = new ProposedAction
        {
            Id = Guid.NewGuid(),
            Kind = "notify-operator",
            Description = $"Notify operations to contact the driver assigned to {asset.Name} and run the connectivity checklist."
        };

        return Task.FromResult((analysis, action));
    }
}
