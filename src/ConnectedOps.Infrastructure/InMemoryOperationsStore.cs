using ConnectedOps.Application;
using ConnectedOps.Domain;

namespace ConnectedOps.Infrastructure;

public sealed class InMemoryOperationsStore : IOperationsStore
{
    private readonly object _gate = new();
    private readonly List<ConnectedAsset> _assets;
    private readonly List<TelemetryEvent> _events = [];
    private readonly List<Incident> _incidents = [];

    public InMemoryOperationsStore(TimeProvider timeProvider)
    {
        var now = timeProvider.GetUtcNow();
        _assets =
        [
            NewAsset("11111111-1111-1111-1111-111111111111", "Vehicle 12", AssetType.Vehicle, "Brighton", now),
            NewAsset("22222222-2222-2222-2222-222222222222", "Driver App 12", AssetType.DriverApp, "Brighton", now),
            NewAsset("33333333-3333-3333-3333-333333333333", "Hub Scanner 04", AssetType.Scanner, "Operations Hub", now),
            NewAsset("44444444-4444-4444-4444-444444444444", "Payment Terminal 04", AssetType.PaymentTerminal, "Operations Hub", now),
            NewAsset("55555555-5555-5555-5555-555555555555", "Vehicle 27", AssetType.Vehicle, "Hove", now),
            NewAsset("66666666-6666-6666-6666-666666666666", "Sensor Gateway 08", AssetType.IoTModule, "School Route North", now)
        ];
    }

    public IReadOnlyList<ConnectedAsset> GetAssets()
    {
        lock (_gate)
        {
            return _assets.OrderBy(asset => asset.Name).ToArray();
        }
    }

    public IReadOnlyList<Incident> GetIncidents()
    {
        lock (_gate)
        {
            return _incidents.ToArray();
        }
    }

    public ConnectedAsset? GetAsset(Guid assetId)
    {
        lock (_gate)
        {
            return _assets.SingleOrDefault(asset => asset.Id == assetId);
        }
    }

    public Incident? GetIncident(Guid incidentId)
    {
        lock (_gate)
        {
            return _incidents.SingleOrDefault(incident => incident.Id == incidentId);
        }
    }

    public IReadOnlyList<TelemetryEvent> GetEvents(Guid assetId)
    {
        lock (_gate)
        {
            return _events
                .Where(item => item.AssetId == assetId)
                .OrderByDescending(item => item.OccurredAt)
                .ToArray();
        }
    }

    public Incident CreateOfflineIncident(Guid assetId, DateTimeOffset occurredAt)
    {
        lock (_gate)
        {
            var existing = _incidents.FirstOrDefault(
                incident => incident.AssetId == assetId && incident.Status is IncidentStatus.Open);
            if (existing is not null)
            {
                return existing;
            }

            var asset = _assets.SingleOrDefault(item => item.Id == assetId)
                ?? throw new KeyNotFoundException($"Asset '{assetId}' was not found.");

            asset.MarkOffline(occurredAt);
            _events.AddRange(
            [
                NewEvent(assetId, occurredAt.AddMinutes(-8), "request.slow", "Status update exceeded the normal latency window.", 480),
                NewEvent(assetId, occurredAt.AddMinutes(-6), "request.slow", "Status update latency increased.", 1_200),
                NewEvent(assetId, occurredAt.AddMinutes(-4), "request.slow", "Status update latency increased again.", 3_400),
                NewEvent(assetId, occurredAt.AddMinutes(-2), "request.timeout", "Three consecutive status requests timed out."),
                NewEvent(assetId, occurredAt, "asset.offline", "No heartbeat received inside the two-minute service window.")
            ]);

            var incident = new Incident
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                Title = $"{asset.Name} stopped reporting",
                Severity = "High",
                OpenedAt = occurredAt
            };

            _incidents.Add(incident);
            return incident;
        }
    }

    public void Reset(DateTimeOffset occurredAt)
    {
        lock (_gate)
        {
            foreach (var asset in _assets)
            {
                asset.MarkHealthy(occurredAt);
            }

            _events.Clear();
            _incidents.Clear();
        }
    }

    private static ConnectedAsset NewAsset(
        string id,
        string name,
        AssetType type,
        string location,
        DateTimeOffset now)
    {
        var asset = new ConnectedAsset
        {
            Id = Guid.Parse(id),
            Name = name,
            Type = type,
            Location = location
        };
        asset.MarkHealthy(now);
        return asset;
    }

    private static TelemetryEvent NewEvent(
        Guid assetId,
        DateTimeOffset occurredAt,
        string eventType,
        string message,
        int? latencyMs = null)
    {
        return new TelemetryEvent(
            Guid.NewGuid(),
            assetId,
            occurredAt,
            eventType,
            message,
            latencyMs);
    }
}
