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
        if (!_tools.TryGetValue(toolName, out var tool))
        {
            throw new OperationsToolException(
                OperationsToolErrorKind.NotFound,
                $"Read-only tool '{toolName}' was not found.");
        }

        if (string.IsNullOrWhiteSpace(caller) || caller.Length > 100)
        {
            throw new OperationsToolException(
                OperationsToolErrorKind.InvalidArguments,
                "Tool caller must be between 1 and 100 characters.");
        }

        var startedAt = TimeProvider.GetUtcNow();
        var startedTimestamp = TimeProvider.GetTimestamp();
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
            throw new OperationsToolException(
                OperationsToolErrorKind.ExecutionFailed,
                $"Read-only tool '{toolName}' failed safely.",
                exception);
        }
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
