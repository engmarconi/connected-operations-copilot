using ConnectedOps.Domain;

namespace ConnectedOps.Application;

public static class DiagnosticSafety
{
    public const string MissingEvidenceReason =
        "no approved runbook evidence was retrieved";

    private static readonly IReadOnlyDictionary<string, string> EventSignals =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["request.slow"] = "latency",
            ["request.timeout"] = "timeout",
            ["asset.offline"] = "offline heartbeat",
            ["session.expired"] = "application session",
            ["device.power"] = "hardware power",
            ["device.enclosure"] = "hardware enclosure",
            ["sensor.fault"] = "sensor hardware"
        };

    public static string BuildRetrievalQuery(
        Incident incident,
        ConnectedAsset asset,
        IReadOnlyList<TelemetryEvent> events)
    {
        var parts = new List<string> { incident.Title, asset.Type.ToString() };
        foreach (var telemetryEvent in events)
        {
            if (EventSignals.TryGetValue(telemetryEvent.EventType, out var signal))
            {
                parts.Add(signal);
            }
        }

        return string.Join(' ', parts);
    }

    public static void EnsureEvidence(int evidenceCount, string providerLabel)
    {
        if (evidenceCount is 0)
        {
            throw new DiagnosticProviderException(
                $"{providerLabel} diagnosis was blocked because {MissingEvidenceReason}.");
        }
    }
}
