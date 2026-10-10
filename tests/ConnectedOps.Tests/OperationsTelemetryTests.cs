using System.Diagnostics;
using System.Diagnostics.Metrics;
using ConnectedOps.Application;
using ConnectedOps.Infrastructure;

namespace ConnectedOps.Tests;

public sealed class OperationsTelemetryTests
{
    private static readonly Guid VehicleId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Offline_scenario_emits_activity_and_diagnosis_metric()
    {
        var activities = new List<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OperationsTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => activities.Add(activity)
        };
        ActivitySource.AddActivityListener(activityListener);

        long diagnoses = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OperationsTelemetry.MeterName
                && instrument.Name == "connectedops.diagnoses_completed")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
        {
            diagnoses += measurement;
        });
        meterListener.Start();

        var timeProvider = new StubTimeProvider(
            new DateTimeOffset(2026, 10, 10, 9, 30, 0, TimeSpan.Zero));
        var service = new OperationsService(
            new InMemoryOperationsStore(timeProvider),
            new MockDiagnosticService(new LocalRunbookRetriever(), timeProvider),
            timeProvider);

        var incident = await service.RunOfflineScenarioAsync(
            VehicleId,
            CancellationToken.None);

        Assert.NotNull(incident.Diagnosis);
        Assert.Contains(
            activities,
            activity => activity.OperationName == "operations.offline_scenario"
                && activity.GetTagItem("asset.id")?.ToString() == VehicleId.ToString()
                && activity.GetTagItem("diagnosis.provider") is not null);
        Assert.True(diagnoses >= 1);
    }

    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
