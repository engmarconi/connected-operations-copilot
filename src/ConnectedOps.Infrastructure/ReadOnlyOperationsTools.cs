using System.Collections.Concurrent;
using System.Text.Json;
using ConnectedOps.Application;

namespace ConnectedOps.Infrastructure;

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
        var request = ParseArguments(arguments);
        var asset = store.GetAsset(request.AssetId)
            ?? throw new OperationsToolException(
                OperationsToolErrorKind.NotFound,
                $"Asset '{request.AssetId}' was not found.");
        var allEvents = store.GetEvents(request.AssetId);
        var events = allEvents.Take(request.Limit).ToArray();
        var output = JsonSerializer.SerializeToElement(new
        {
            asset = new
            {
                asset.Id,
                asset.Name,
                asset.Type,
                asset.Location,
                asset.Health,
                asset.LastSeenAt
            },
            events,
            returnedCount = events.Length,
            hasMore = allEvents.Count > events.Length
        });
        var auditSummary =
            $"Returned {events.Length} telemetry event(s) for asset '{asset.Id}'.";

        return Task.FromResult((output, auditSummary));
    }

    private static RecentTelemetryRequest ParseArguments(JsonElement arguments)
    {
        if (arguments.ValueKind is not JsonValueKind.Object)
        {
            throw InvalidArguments(
                "Tool arguments must be a JSON object.");
        }

        var properties = arguments.EnumerateObject().ToArray();
        var unknownArgument = properties.FirstOrDefault(
            property => !AllowedArguments.Contains(property.Name));
        if (unknownArgument.Name is not null)
        {
            throw InvalidArguments(
                $"Unknown tool argument '{unknownArgument.Name}'.");
        }

        if (!arguments.TryGetProperty("assetId", out var assetIdValue)
            || assetIdValue.ValueKind is not JsonValueKind.String
            || !Guid.TryParse(assetIdValue.GetString(), out var assetId)
            || assetId == Guid.Empty)
        {
            throw InvalidArguments(
                "Tool argument 'assetId' must be a non-empty UUID.");
        }

        if (!arguments.TryGetProperty("limit", out var limitValue)
            || !limitValue.TryGetInt32(out var limit)
            || limit is < 1 or > 20)
        {
            throw InvalidArguments(
                "Tool argument 'limit' must be an integer between 1 and 20.");
        }

        return new RecentTelemetryRequest(assetId, limit);
    }

    private static OperationsToolException InvalidArguments(string message)
    {
        return new OperationsToolException(
            OperationsToolErrorKind.InvalidArguments,
            message);
    }

    private sealed record RecentTelemetryRequest(Guid AssetId, int Limit);
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
