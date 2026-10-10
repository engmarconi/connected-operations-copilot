using System.Diagnostics;
using System.Text.Json;

namespace ConnectedOps.Application;

public sealed record OperationsToolDefinition(
    string Name,
    string Description,
    bool IsReadOnly,
    int TimeoutMilliseconds,
    JsonElement InputSchema);

public sealed record OperationsToolResult(
    string ToolName,
    JsonElement Output,
    DateTimeOffset CompletedAt);

public sealed record ToolAuditRecord(
    Guid Id,
    string ToolName,
    string Caller,
    DateTimeOffset StartedAt,
    long DurationMilliseconds,
    bool Succeeded,
    string Summary);

public enum OperationsToolErrorKind
{
    InvalidArguments,
    NotFound,
    TimedOut,
    ExecutionFailed
}

public sealed class OperationsToolException(
    OperationsToolErrorKind kind,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public OperationsToolErrorKind Kind { get; } = kind;
}

public interface IReadOnlyOperationsTool
{
    OperationsToolDefinition Definition { get; }

    Task<(JsonElement Output, string AuditSummary)> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken);
}

public interface IToolAuditStore
{
    void Append(ToolAuditRecord record);
    IReadOnlyList<ToolAuditRecord> GetRecent(int limit);
}

public sealed class OperationsToolService
{
    private readonly IReadOnlyDictionary<string, IReadOnlyOperationsTool> _tools;

    public OperationsToolService(
        IEnumerable<IReadOnlyOperationsTool> tools,
        IToolAuditStore auditStore,
        TimeProvider timeProvider)
    {
        AuditStore = auditStore;
        TimeProvider = timeProvider;
        _tools = tools.ToDictionary(
            tool => tool.Definition.Name,
            StringComparer.Ordinal);
        if (_tools.Count is 0)
        {
            throw new InvalidOperationException("At least one read-only operations tool is required.");
        }

        if (_tools.Values.Any(tool => !tool.Definition.IsReadOnly))
        {
            throw new InvalidOperationException(
                "The operations tool registry accepts read-only tools only.");
        }
    }

    private IToolAuditStore AuditStore { get; }
    private TimeProvider TimeProvider { get; }

    public IReadOnlyList<OperationsToolDefinition> GetDefinitions()
    {
        return _tools.Values
            .Select(tool => tool.Definition)
            .OrderBy(definition => definition.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<ToolAuditRecord> GetAuditTrail(int limit = 20)
    {
        if (limit is < 1 or > 100)
        {
            throw new OperationsToolException(
                OperationsToolErrorKind.InvalidArguments,
                "Audit limit must be between 1 and 100.");
        }

        return AuditStore.GetRecent(limit);
    }

    public async Task<OperationsToolResult> ExecuteAsync(
        string toolName,
        JsonElement arguments,
        string caller,
        CancellationToken cancellationToken)
    {
        using var activity = OperationsTelemetry.ActivitySource.StartActivity(
            "operations.tool_execute");
        activity?.SetTag("tool.name", toolName);
        activity?.SetTag("tool.caller", caller);
        OperationsTelemetry.ToolExecutions.Add(
            1,
            new KeyValuePair<string, object?>("tool.name", toolName),
            new KeyValuePair<string, object?>("tool.caller", caller));

        if (string.IsNullOrWhiteSpace(caller) || caller.Length > 100)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "invalid caller");
            throw new OperationsToolException(
                OperationsToolErrorKind.InvalidArguments,
                "Tool caller must be between 1 and 100 characters.");
        }

        var startedAt = TimeProvider.GetUtcNow();
        var startedTimestamp = TimeProvider.GetTimestamp();
        if (!_tools.TryGetValue(toolName, out var tool))
        {
            var message = $"Read-only tool '{toolName}' was not found.";
            AppendAudit(
                toolName,
                caller,
                startedAt,
                startedTimestamp,
                succeeded: false,
                message);
            activity?.SetStatus(ActivityStatusCode.Error, message);
            throw new OperationsToolException(
                OperationsToolErrorKind.NotFound,
                message);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutSource.CancelAfter(tool.Definition.TimeoutMilliseconds);

        try
        {
            var (output, auditSummary) = await tool.ExecuteAsync(
                arguments,
                timeoutSource.Token);
            AppendAudit(
                toolName,
                caller,
                startedAt,
                startedTimestamp,
                succeeded: true,
                auditSummary);
            activity?.SetTag("tool.succeeded", true);
            return new OperationsToolResult(
                toolName,
                output,
                TimeProvider.GetUtcNow());
        }
        catch (OperationCanceledException exception)
            when (!cancellationToken.IsCancellationRequested)
        {
            AppendAudit(
                toolName,
                caller,
                startedAt,
                startedTimestamp,
                succeeded: false,
                "Tool execution exceeded its read-only timeout.");
            activity?.SetStatus(ActivityStatusCode.Error, "timed out");
            throw new OperationsToolException(
                OperationsToolErrorKind.TimedOut,
                $"Read-only tool '{toolName}' timed out.",
                exception);
        }
        catch (OperationsToolException exception)
        {
            AppendAudit(
                toolName,
                caller,
                startedAt,
                startedTimestamp,
                succeeded: false,
                exception.Message);
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
        catch (Exception exception)
        {
            AppendAudit(
                toolName,
                caller,
                startedAt,
                startedTimestamp,
                succeeded: false,
                "Tool execution failed safely.");
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw new OperationsToolException(
                OperationsToolErrorKind.ExecutionFailed,
                $"Read-only tool '{toolName}' failed safely.",
                exception);
        }
    }

    public void RecordRejectedAttempt(
        string toolName,
        string caller,
        string summary)
    {
        var safeToolName = string.IsNullOrWhiteSpace(toolName)
            ? "(missing)"
            : toolName[..Math.Min(toolName.Length, 100)];
        var safeCaller = string.IsNullOrWhiteSpace(caller)
            ? "(unknown)"
            : caller[..Math.Min(caller.Length, 100)];
        var safeSummary = string.IsNullOrWhiteSpace(summary)
            ? "Tool request was rejected by policy."
            : summary[..Math.Min(summary.Length, 500)];
        var startedAt = TimeProvider.GetUtcNow();
        AppendAudit(
            safeToolName,
            safeCaller,
            startedAt,
            TimeProvider.GetTimestamp(),
            succeeded: false,
            safeSummary);
    }

    private void AppendAudit(
        string toolName,
        string caller,
        DateTimeOffset startedAt,
        long startedTimestamp,
        bool succeeded,
        string summary)
    {
        AuditStore.Append(new ToolAuditRecord(
            Guid.NewGuid(),
            toolName,
            caller.Trim(),
            startedAt,
            (long)TimeProvider.GetElapsedTime(startedTimestamp).TotalMilliseconds,
            succeeded,
            summary));
    }
}
