namespace ConnectedOps.Domain;

public enum AssetType
{
    Vehicle,
    DriverApp,
    PaymentTerminal,
    Scanner,
    IoTModule
}

public enum HealthState
{
    Healthy,
    Warning,
    Offline
}

public enum IncidentStatus
{
    Open,
    ActionApproved,
    Resolved
}

public sealed class ConnectedAsset
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required AssetType Type { get; init; }
    public required string Location { get; init; }
    public HealthState Health { get; private set; } = HealthState.Healthy;
    public DateTimeOffset LastSeenAt { get; private set; }

    public void MarkOffline(DateTimeOffset occurredAt)
    {
        Health = HealthState.Offline;
        LastSeenAt = occurredAt;
    }

    public void MarkHealthy(DateTimeOffset occurredAt)
    {
        Health = HealthState.Healthy;
        LastSeenAt = occurredAt;
    }
}

public sealed record TelemetryEvent(
    Guid Id,
    Guid AssetId,
    DateTimeOffset OccurredAt,
    string EventType,
    string Message,
    int? LatencyMs = null);

public sealed record RunbookCitation(
    string DocumentId,
    string Title,
    string Section,
    string Excerpt);

public sealed record RunbookChunk(
    string DocumentId,
    string Title,
    string Section,
    string Content,
    IReadOnlyList<string> Keywords);

public sealed record DiagnosticAnalysis(
    string Summary,
    IReadOnlyList<string> ProbableCauses,
    IReadOnlyList<string> RecommendedSteps,
    IReadOnlyList<RunbookCitation> Citations,
    decimal Confidence,
    string Provider,
    DateTimeOffset GeneratedAt);

public sealed class ProposedAction
{
    public required Guid Id { get; init; }
    public required string Kind { get; init; }
    public required string Description { get; init; }
    public bool RequiresApproval { get; init; } = true;
    public bool IsApproved { get; private set; }
    public string? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public string? SimulatedResult { get; private set; }

    public void Approve(string approvedBy, DateTimeOffset approvedAt, string simulatedResult)
    {
        IsApproved = true;
        ApprovedBy = approvedBy;
        ApprovedAt = approvedAt;
        SimulatedResult = simulatedResult;
    }
}

public sealed class Incident
{
    public required Guid Id { get; init; }
    public required Guid AssetId { get; init; }
    public required string Title { get; init; }
    public required string Severity { get; init; }
    public required DateTimeOffset OpenedAt { get; init; }
    public IncidentStatus Status { get; private set; } = IncidentStatus.Open;
    public DiagnosticAnalysis? Diagnosis { get; private set; }
    public ProposedAction? ProposedAction { get; private set; }

    public void AddDiagnosis(DiagnosticAnalysis diagnosis, ProposedAction proposedAction)
    {
        Diagnosis = diagnosis;
        ProposedAction = proposedAction;
    }

    public void ApproveAction(string approvedBy, DateTimeOffset approvedAt)
    {
        if (ProposedAction is null)
        {
            throw new InvalidOperationException("The incident has no proposed action.");
        }

        ProposedAction.Approve(
            approvedBy,
            approvedAt,
            "Notification queued for the simulated operations channel.");
        Status = IncidentStatus.ActionApproved;
    }
}
