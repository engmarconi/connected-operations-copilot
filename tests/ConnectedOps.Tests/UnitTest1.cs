using ConnectedOps.Application;
using ConnectedOps.Domain;
using ConnectedOps.Infrastructure;

namespace ConnectedOps.Tests;

public sealed class OperationsServiceTests
{
    private static readonly Guid VehicleId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Dashboard_starts_with_all_assets_healthy()
    {
        var service = CreateService();

        var dashboard = service.GetDashboard();

        Assert.Equal(6, dashboard.Summary.TotalAssets);
        Assert.Equal(6, dashboard.Summary.HealthyAssets);
        Assert.Equal(0, dashboard.Summary.OfflineAssets);
        Assert.Empty(dashboard.Incidents);
    }

    [Fact]
    public async Task Offline_scenario_creates_cited_diagnosis_and_unapproved_action()
    {
        var service = CreateService();

        var incident = await service.RunOfflineScenarioAsync(
            VehicleId,
            CancellationToken.None);

        Assert.Equal("High", incident.Severity);
        Assert.NotNull(incident.Diagnosis);
        Assert.Equal("mock-generator/local-runbook-retrieval-v1", incident.Diagnosis.Provider);
        Assert.Equal(2, incident.Diagnosis.Citations.Count);
        Assert.Equal("RB-CONN-001", incident.Diagnosis.Citations[0].DocumentId);
        Assert.Equal("RB-APP-003", incident.Diagnosis.Citations[1].DocumentId);
        Assert.All(
            incident.Diagnosis.Citations,
            citation => Assert.False(string.IsNullOrWhiteSpace(citation.DocumentId)));
        Assert.NotNull(incident.ProposedAction);
        Assert.True(incident.ProposedAction.RequiresApproval);
        Assert.False(incident.ProposedAction.IsApproved);

        var dashboard = service.GetDashboard();
        Assert.Equal(1, dashboard.Summary.OfflineAssets);
        Assert.Equal(1, dashboard.Summary.OpenIncidents);
    }

    [Fact]
    public async Task Notification_is_only_simulated_after_human_approval()
    {
        var service = CreateService();
        var incident = await service.RunOfflineScenarioAsync(
            VehicleId,
            CancellationToken.None);

        var approved = service.ApproveNotification(incident.Id, "Marco");

        Assert.Equal(IncidentStatus.ActionApproved, approved.Status);
        Assert.True(approved.ProposedAction!.IsApproved);
        Assert.Equal("Marco", approved.ProposedAction.ApprovedBy);
        Assert.Contains("simulated", approved.ProposedAction.SimulatedResult);
    }

    [Fact]
    public async Task Retriever_ranks_relevant_runbooks_and_returns_one_chunk_per_document()
    {
        var retriever = new LocalRunbookRetriever();

        var chunks = await retriever.RetrieveAsync(
            "vehicle request latency timeout offline heartbeat driver reporting",
            maxResults: 3,
            CancellationToken.None);

        Assert.Equal(6, retriever.Chunks.Count);
        Assert.Equal("RB-CONN-001", chunks[0].DocumentId);
        Assert.Equal("RB-APP-003", chunks[1].DocumentId);
        Assert.Equal(chunks.Count, chunks.Select(chunk => chunk.DocumentId).Distinct().Count());
    }

    [Fact]
    public async Task Diagnostic_citations_are_supplied_by_the_retriever()
    {
        var timeProvider = new StubTimeProvider(
            new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero));
        var store = new InMemoryOperationsStore(timeProvider);
        var diagnosticService = new MockDiagnosticService(
            new StubRunbookRetriever(),
            timeProvider);
        var service = new OperationsService(store, diagnosticService, timeProvider);

        var incident = await service.RunOfflineScenarioAsync(
            VehicleId,
            CancellationToken.None);

        var citation = Assert.Single(incident.Diagnosis!.Citations);
        Assert.Equal("RB-TEST-001", citation.DocumentId);
        Assert.Equal("Evidence supplied by the retrieval boundary.", citation.Excerpt);
    }

    [Fact]
    public async Task Feature_hash_embeddings_are_deterministic_and_normalized()
    {
        var service = new FeatureHashEmbeddingService();

        var first = await service.EmbedAsync(
            "mobile network latency timeout heartbeat",
            CancellationToken.None);
        var second = await service.EmbedAsync(
            "mobile network latency timeout heartbeat",
            CancellationToken.None);

        Assert.Equal(256, first.Length);
        Assert.Equal(first, second);
        Assert.InRange(
            Math.Sqrt(first.Sum(value => value * value)),
            0.999,
            1.001);
    }

    [Fact]
    public async Task Pgvector_retriever_indexes_and_ranks_embedded_runbooks_when_configured()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "RUNBOOK_PGVECTOR_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var retriever = new PgvectorRunbookRetriever(
            connectionString,
            new FeatureHashEmbeddingService());

        var chunks = await retriever.RetrieveAsync(
            "vehicle request latency timeout offline heartbeat driver reporting",
            maxResults: 2,
            CancellationToken.None);

        Assert.Equal("pgvector-feature-hash-256-v1", retriever.ProviderId);
        Assert.Equal(2, chunks.Count);
        Assert.Equal("RB-CONN-001", chunks[0].DocumentId);
        Assert.Contains(chunks, chunk => chunk.DocumentId is "RB-APP-003");
    }

    [Fact]
    public async Task Concurrent_scenario_requests_share_one_diagnosis()
    {
        var timeProvider = new StubTimeProvider(
            new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero));
        var store = new InMemoryOperationsStore(timeProvider);
        var diagnosticService = new CountingDiagnosticService(
            new MockDiagnosticService(
                new LocalRunbookRetriever(),
                timeProvider));
        var service = new OperationsService(store, diagnosticService, timeProvider);

        var incidents = await Task.WhenAll(
            service.RunOfflineScenarioAsync(VehicleId, CancellationToken.None),
            service.RunOfflineScenarioAsync(VehicleId, CancellationToken.None));

        Assert.Equal(incidents[0].Id, incidents[1].Id);
        Assert.Equal(1, diagnosticService.CallCount);
        Assert.Single(service.GetDashboard().Incidents);
    }

    private static OperationsService CreateService()
    {
        var timeProvider = new StubTimeProvider(
            new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero));
        var store = new InMemoryOperationsStore(timeProvider);
        var diagnosticService = new MockDiagnosticService(
            new LocalRunbookRetriever(),
            timeProvider);
        return new OperationsService(store, diagnosticService, timeProvider);
    }

    private sealed class StubRunbookRetriever : IRunbookRetriever
    {
        public string ProviderId => "test-retrieval";

        public Task<IReadOnlyList<RunbookChunk>> RetrieveAsync(
            string query,
            int maxResults,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<RunbookChunk> result =
            [
                new(
                    "RB-TEST-001",
                    "Test Runbook",
                    "1.0 — Test evidence",
                    "Evidence supplied by the retrieval boundary.",
                    ["test"])
            ];
            return Task.FromResult(result);
        }
    }

    private sealed class CountingDiagnosticService(IDiagnosticService inner)
        : IDiagnosticService
    {
        private int _callCount;

        public int CallCount => _callCount;

        public async Task<(DiagnosticAnalysis Analysis, ProposedAction Action)> AnalyzeAsync(
            Incident incident,
            ConnectedAsset asset,
            IReadOnlyList<TelemetryEvent> events,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Delay(25, cancellationToken);
            return await inner.AnalyzeAsync(
                incident,
                asset,
                events,
                cancellationToken);
        }
    }

    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
