using ConnectedOps.Application;
using ConnectedOps.Domain;
using Npgsql;
using Pgvector;
using Pgvector.Npgsql;

namespace ConnectedOps.Infrastructure;

public sealed class PgvectorRunbookRetriever : IRunbookRetriever, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ITextEmbeddingService _embeddingService;
    private readonly IReadOnlyList<RunbookChunk> _chunks;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public PgvectorRunbookRetriever(
        string connectionString,
        ITextEmbeddingService embeddingService)
        : this(
            connectionString,
            embeddingService,
            LocalRunbookRetriever.LoadEmbeddedRunbooks())
    {
    }

    internal PgvectorRunbookRetriever(
        string connectionString,
        ITextEmbeddingService embeddingService,
        IEnumerable<RunbookChunk> chunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(embeddingService);

        _embeddingService = embeddingService;
        _chunks = chunks.ToArray();
        if (_chunks.Count is 0)
        {
            throw new InvalidOperationException("At least one runbook chunk is required.");
        }

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        _dataSource = dataSourceBuilder.Build();
    }

    public string ProviderId => $"pgvector-{_embeddingService.ModelId}";

    public async Task<IReadOnlyList<RunbookChunk>> RetrieveAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        await EnsureInitializedAsync(cancellationToken);
        var embedding = await _embeddingService.EmbedAsync(
            query,
            EmbeddingPurpose.Query,
            cancellationToken);

        const string sql = """
            WITH ranked AS (
                SELECT
                    document_id,
                    title,
                    section,
                    content,
                    keywords,
                    embedding <=> $1 AS distance,
                    ROW_NUMBER() OVER (
                        PARTITION BY document_id
                        ORDER BY embedding <=> $1
                    ) AS document_rank
                FROM runbook_chunks
            )
            SELECT document_id, title, section, content, keywords
            FROM ranked
            WHERE document_rank = 1
            ORDER BY distance, document_id
            LIMIT $2;
            """;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(new Vector(embedding));
        command.Parameters.AddWithValue(maxResults);

        var results = new List<RunbookChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new RunbookChunk(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<string[]>(4)));
        }

        return results;
    }

    public async ValueTask DisposeAsync()
    {
        _initializationGate.Dispose();
        await _dataSource.DisposeAsync();
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using (var extensionCommand = new NpgsqlCommand(
                "CREATE EXTENSION IF NOT EXISTS vector;",
                connection))
            {
                await extensionCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            connection.ReloadTypes();

            var dimensions = _embeddingService.Dimensions;
            if (dimensions is < 1 or > 2_000)
            {
                throw new InvalidOperationException(
                    $"Embedding dimensions must be between 1 and 2000, but were {dimensions}.");
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var lockCommand = new NpgsqlCommand(
                "SELECT pg_advisory_xact_lock(16042026);",
                connection,
                transaction))
            {
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            // Recreate so switching between feature-hash and model embeddings
            // cannot leave a mismatched vector dimension in place.
            await using (var dropCommand = new NpgsqlCommand(
                "DROP TABLE IF EXISTS runbook_chunks;",
                connection,
                transaction))
            {
                await dropCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            var createTableSql = $"""
                CREATE TABLE runbook_chunks (
                    document_id text NOT NULL,
                    title text NOT NULL,
                    section text NOT NULL,
                    content text NOT NULL,
                    keywords text[] NOT NULL,
                    embedding vector({dimensions}) NOT NULL,
                    embedding_model text NOT NULL,
                    PRIMARY KEY (document_id, section)
                );
                """;
            await using (var tableCommand = new NpgsqlCommand(
                createTableSql,
                connection,
                transaction))
            {
                await tableCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var chunk in _chunks)
            {
                await InsertChunkAsync(connection, transaction, chunk, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            await using (var indexCommand = new NpgsqlCommand(
                """
                CREATE INDEX IF NOT EXISTS runbook_chunks_embedding_hnsw
                ON runbook_chunks USING hnsw (embedding vector_cosine_ops);
                """,
                connection))
            {
                await indexCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task InsertChunkAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        RunbookChunk chunk,
        CancellationToken cancellationToken)
    {
        var keywords = string.Join(' ', chunk.Keywords);
        var embeddingText = $"{chunk.Title}\n{chunk.Section}\n{keywords} {keywords}\n{chunk.Content}";
        var embedding = await _embeddingService.EmbedAsync(
            embeddingText,
            EmbeddingPurpose.Document,
            cancellationToken);

        const string sql = """
            INSERT INTO runbook_chunks (
                document_id,
                title,
                section,
                content,
                keywords,
                embedding,
                embedding_model
            )
            VALUES ($1, $2, $3, $4, $5, $6, $7);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(chunk.DocumentId);
        command.Parameters.AddWithValue(chunk.Title);
        command.Parameters.AddWithValue(chunk.Section);
        command.Parameters.AddWithValue(chunk.Content);
        command.Parameters.AddWithValue(chunk.Keywords.ToArray());
        command.Parameters.AddWithValue(new Vector(embedding));
        command.Parameters.AddWithValue(_embeddingService.ModelId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
