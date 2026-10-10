using System.Collections.Concurrent;
using System.Text.Json;
using ConnectedOps.Application;
using ConnectedOps.Domain;

namespace ConnectedOps.Infrastructure;

internal static class ReadOnlyToolArguments
{
    public static void EnsureObject(
        JsonElement arguments,
        IReadOnlySet<string> allowedArguments)
    {
        if (arguments.ValueKind is not JsonValueKind.Object)
        {
            throw InvalidArguments("Tool arguments must be a JSON object.");
        }

        var unknownArgument = arguments.EnumerateObject()
            .Select(property => property.Name)
            .FirstOrDefault(name => !allowedArguments.Contains(name));
        if (unknownArgument is not null)
        {
            throw InvalidArguments($"Unknown tool argument '{unknownArgument}'.");
        }
    }

    public static Guid ReadAssetId(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("assetId", out var assetIdValue)
            || assetIdValue.ValueKind is not JsonValueKind.String
            || !Guid.TryParse(assetIdValue.GetString(), out var assetId)
            || assetId == Guid.Empty)
        {
            throw InvalidArguments(
                "Tool argument 'assetId' must be a non-empty UUID.");
        }

        return assetId;
    }

    public static int ReadLimit(JsonElement arguments, int maximum)
    {
        if (!arguments.TryGetProperty("limit", out var limitValue)
            || !limitValue.TryGetInt32(out var limit)
            || limit < 1
            || limit > maximum)
        {
            throw InvalidArguments(
                $"Tool argument 'limit' must be an integer between 1 and {maximum}.");
        }

        return limit;
    }

    public static OperationsToolException InvalidArguments(string message)
    {
        return new OperationsToolException(
            OperationsToolErrorKind.InvalidArguments,
            message);
    }

    public static object SummarizeAsset(ConnectedAsset asset)
    {
        return new
        {
            id = asset.Id,
            name = asset.Name,
            type = asset.Type.ToString(),
            location = asset.Location,
            health = asset.Health.ToString(),
            lastSeenAt = asset.LastSeenAt
        };
    }
}

public sealed class RecentAssetTelemetryTool(IOperationsStore store)
    : IReadOnlyOperationsTool
{
    private static readonly HashSet<string> AllowedArguments =
        ["assetId", "limit"];

    public OperationsToolDefinition Definition { get; } = new(
        "recent-asset-telemetry",
        "Returns the most recent synthetic telemetry events for one connected asset.",
        IsReadOnly: true,
        TimeoutMilliseconds: 2_000,
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                assetId = new
                {
                    type = "string",
                    format = "uuid",
                    description = "Connected asset identifier."
                },
                limit = new
                {
                    type = "integer",
                    minimum = 1,
                    maximum = 20,
                    description = "Maximum telemetry events to return."
                }
            },
            required = new[] { "assetId", "limit" },
            additionalProperties = false
        }));

    public Task<(JsonElement Output, string AuditSummary)> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyToolArguments.EnsureObject(arguments, AllowedArguments);
        var assetId = ReadOnlyToolArguments.ReadAssetId(arguments);
        var limit = ReadOnlyToolArguments.ReadLimit(arguments, maximum: 20);
        var asset = store.GetAsset(assetId)
            ?? throw new OperationsToolException(
                OperationsToolErrorKind.NotFound,
                $"Asset '{assetId}' was not found.");
        var allEvents = store.GetEvents(assetId);
        var events = allEvents.Take(limit).ToArray();
        var output = JsonSerializer.SerializeToElement(new
        {
            asset = ReadOnlyToolArguments.SummarizeAsset(asset),
            events,
            returnedCount = events.Length,
            hasMore = allEvents.Count > events.Length
        });
        var auditSummary =
            $"Returned {events.Length} telemetry event(s) for asset '{asset.Id}'.";

        return Task.FromResult((output, auditSummary));
    }
}

public sealed class FleetComparisonTool(IOperationsStore store)
    : IReadOnlyOperationsTool
{
    private static readonly HashSet<string> AllowedArguments =
        ["assetId", "limit"];

    public OperationsToolDefinition Definition { get; } = new(
        "fleet-comparison",
        "Compares one asset's health with peers in the same location and across the synthetic fleet.",
        IsReadOnly: true,
        TimeoutMilliseconds: 2_000,
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                assetId = new
                {
                    type = "string",
                    format = "uuid",
                    description = "Connected asset identifier used as the comparison focus."
                },
                limit = new
                {
                    type = "integer",
                    minimum = 1,
                    maximum = 20,
                    description = "Maximum peer assets to return."
                }
            },
            required = new[] { "assetId", "limit" },
            additionalProperties = false
        }));

    public Task<(JsonElement Output, string AuditSummary)> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyToolArguments.EnsureObject(arguments, AllowedArguments);
        var assetId = ReadOnlyToolArguments.ReadAssetId(arguments);
        var limit = ReadOnlyToolArguments.ReadLimit(arguments, maximum: 20);
        var assets = store.GetAssets();
        var asset = assets.SingleOrDefault(item => item.Id == assetId)
            ?? throw new OperationsToolException(
                OperationsToolErrorKind.NotFound,
                $"Asset '{assetId}' was not found.");

        var peers = assets
            .Where(item => item.Id != asset.Id)
            .OrderBy(item => string.Equals(
                item.Location,
                asset.Location,
                StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
        var returnedPeers = peers.Take(limit).ToArray();
        var sameLocationOffline = peers.Count(item =>
            string.Equals(item.Location, asset.Location, StringComparison.OrdinalIgnoreCase)
            && item.Health is HealthState.Offline);
        var output = JsonSerializer.SerializeToElement(new
        {
            asset = ReadOnlyToolArguments.SummarizeAsset(asset),
            fleetSummary = new
            {
                totalAssets = assets.Count,
                healthyAssets = assets.Count(item => item.Health is HealthState.Healthy),
                warningAssets = assets.Count(item => item.Health is HealthState.Warning),
                offlineAssets = assets.Count(item => item.Health is HealthState.Offline),
                sameLocationPeerCount = peers.Count(item =>
                    string.Equals(item.Location, asset.Location, StringComparison.OrdinalIgnoreCase)),
                sameLocationOfflinePeers = sameLocationOffline
            },
            peers = returnedPeers.Select(ReadOnlyToolArguments.SummarizeAsset).ToArray(),
            returnedCount = returnedPeers.Length,
            hasMore = peers.Length > returnedPeers.Length
        });
        var auditSummary =
            $"Compared asset '{asset.Id}' with {returnedPeers.Length} peer asset(s).";

        return Task.FromResult((output, auditSummary));
    }
}

public sealed class IncidentHistoryTool(IOperationsStore store)
    : IReadOnlyOperationsTool
{
    private static readonly HashSet<string> AllowedArguments =
        ["assetId", "limit"];

    public OperationsToolDefinition Definition { get; } = new(
        "incident-history",
        "Returns recent incidents for one connected asset with status and diagnosis presence only.",
        IsReadOnly: true,
        TimeoutMilliseconds: 2_000,
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                assetId = new
                {
                    type = "string",
                    format = "uuid",
                    description = "Connected asset identifier."
                },
                limit = new
                {
                    type = "integer",
                    minimum = 1,
                    maximum = 10,
                    description = "Maximum incidents to return."
                }
            },
            required = new[] { "assetId", "limit" },
            additionalProperties = false
        }));

    public Task<(JsonElement Output, string AuditSummary)> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlyToolArguments.EnsureObject(arguments, AllowedArguments);
        var assetId = ReadOnlyToolArguments.ReadAssetId(arguments);
        var limit = ReadOnlyToolArguments.ReadLimit(arguments, maximum: 10);
        var asset = store.GetAsset(assetId)
            ?? throw new OperationsToolException(
                OperationsToolErrorKind.NotFound,
                $"Asset '{assetId}' was not found.");
        var allIncidents = store.GetIncidents()
            .Where(incident => incident.AssetId == assetId)
            .OrderByDescending(incident => incident.OpenedAt)
            .ToArray();
        var incidents = allIncidents
            .Take(limit)
            .Select(incident => new
            {
                id = incident.Id,
                title = incident.Title,
                severity = incident.Severity,
                status = incident.Status.ToString(),
                openedAt = incident.OpenedAt,
                hasDiagnosis = incident.Diagnosis is not null,
                diagnosisProvider = incident.Diagnosis?.Provider,
                actionRequiresApproval = incident.ProposedAction?.RequiresApproval,
                actionApproved = incident.ProposedAction?.IsApproved ?? false
            })
            .ToArray();
        var output = JsonSerializer.SerializeToElement(new
        {
            asset = ReadOnlyToolArguments.SummarizeAsset(asset),
            incidents,
            returnedCount = incidents.Length,
            hasMore = allIncidents.Length > incidents.Length
        });
        var auditSummary =
            $"Returned {incidents.Length} incident(s) for asset '{asset.Id}'.";

        return Task.FromResult((output, auditSummary));
    }
}

public sealed class InMemoryToolAuditStore : IToolAuditStore
{
    private const int Capacity = 100;
    private readonly ConcurrentQueue<ToolAuditRecord> _records = [];

    public void Append(ToolAuditRecord record)
    {
        _records.Enqueue(record);
        while (_records.Count > Capacity)
        {
            _records.TryDequeue(out _);
        }
    }

    public IReadOnlyList<ToolAuditRecord> GetRecent(int limit)
    {
        return _records
            .Reverse()
            .Take(limit)
            .ToArray();
    }
}
