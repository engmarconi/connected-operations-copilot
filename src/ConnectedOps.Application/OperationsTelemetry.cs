using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ConnectedOps.Application;

public static class OperationsTelemetry
{
    public const string ServiceName = "connected-operations-api";
    public const string ActivitySourceName = "ConnectedOps.Operations";
    public const string MeterName = "ConnectedOps.Operations";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> OfflineScenarios =
        Meter.CreateCounter<long>(
            "connectedops.offline_scenarios",
            unit: "{scenario}",
            description: "Offline simulation scenarios started.");

    public static readonly Counter<long> DiagnosesCompleted =
        Meter.CreateCounter<long>(
            "connectedops.diagnoses_completed",
            unit: "{diagnosis}",
            description: "Diagnoses completed successfully.");

    public static readonly Counter<long> DiagnosesFailed =
        Meter.CreateCounter<long>(
            "connectedops.diagnoses_failed",
            unit: "{diagnosis}",
            description: "Diagnoses that failed closed.");

    public static readonly Counter<long> Approvals =
        Meter.CreateCounter<long>(
            "connectedops.approvals",
            unit: "{approval}",
            description: "Human approvals of proposed actions.");

    public static readonly Counter<long> ToolExecutions =
        Meter.CreateCounter<long>(
            "connectedops.tool_executions",
            unit: "{call}",
            description: "Read-only tool executions attempted.");
}
