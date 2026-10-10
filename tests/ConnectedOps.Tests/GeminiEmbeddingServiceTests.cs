using System.Net;
using System.Text;
using System.Text.Json;
using ConnectedOps.Application;
using ConnectedOps.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectedOps.Tests;

public sealed class GeminiEmbeddingServiceTests
{
    [Fact]
    public async Task Embedding_client_sends_task_type_and_normalizes_output()
    {
        string? requestJson = null;
        string? apiKey = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            apiKey = request.Headers.GetValues("x-goog-api-key").Single();
            return JsonResponse(new
            {
                embedding = new
                {
                    values = new[] { 3f, 4f, 0f }
                }
            });
        });
        var options = Options(dimensions: 3);
        var client = new GeminiEmbeddingClient(new HttpClient(handler), options);

        var embedding = await client.EmbedAsync(
            "mobile network latency timeout heartbeat",
            EmbeddingPurpose.Query,
            CancellationToken.None);

        Assert.Equal("test-key", apiKey);
        Assert.Equal([0.6f, 0.8f, 0f], embedding);
        using var document = JsonDocument.Parse(requestJson!);
        var root = document.RootElement;
        Assert.Equal("models/gemini-embedding-001", root.GetProperty("model").GetString());
        Assert.Equal("RETRIEVAL_QUERY", root.GetProperty("taskType").GetString());
        Assert.Equal(3, root.GetProperty("outputDimensionality").GetInt32());
        Assert.Equal(
            "mobile network latency timeout heartbeat",
            root.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Embedding_client_uses_document_task_type_for_runbook_chunks()
    {
        string? requestJson = null;
        var handler = new StubHttpMessageHandler(async request =>
        {
            requestJson = await request.Content!.ReadAsStringAsync();
            return JsonResponse(new
            {
                embedding = new
                {
                    values = new[] { 1f, 0f }
                }
            });
        });
        var client = new GeminiEmbeddingClient(
            new HttpClient(handler),
            Options(dimensions: 2));

        await client.EmbedAsync(
            "Mobile Connectivity Runbook",
            EmbeddingPurpose.Document,
            CancellationToken.None);

        using var document = JsonDocument.Parse(requestJson!);
        Assert.Equal(
            "RETRIEVAL_DOCUMENT",
            document.RootElement.GetProperty("taskType").GetString());
    }

    [Fact]
    public async Task Embedding_service_exposes_model_identity_and_dimensions()
    {
        var client = new StubGeminiEmbeddingClient([0.5f, 0.5f, 0.5f, 0.5f]);
        var service = new GeminiEmbeddingService(client, Options(dimensions: 4));

        var embedding = await service.EmbedAsync(
            "timeout heartbeat",
            EmbeddingPurpose.Query,
            CancellationToken.None);

        Assert.Equal(4, service.Dimensions);
        Assert.Equal("gemini-embedding-001-4", service.ModelId);
        Assert.Equal(EmbeddingPurpose.Query, client.LastPurpose);
        Assert.Equal(4, embedding.Length);
    }

    [Fact]
    public void Dependency_injection_selects_gemini_embeddings_for_pgvector()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RunbookRetrieval:Mode"] = "pgvector",
                ["ConnectionStrings:Runbooks"] =
                    "Host=127.0.0.1;Port=54329;Database=connectedops;Username=connectedops;Password=connectedops_dev",
                ["Embeddings:Provider"] = "gemini",
                ["Embeddings:Gemini:ApiKey"] = "test-key",
                ["Embeddings:Gemini:Dimensions"] = "768"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddConnectedOperations(configuration)
            .BuildServiceProvider();

        var embeddingService = provider.GetRequiredService<ITextEmbeddingService>();
        Assert.IsType<GeminiEmbeddingService>(embeddingService);
        Assert.Equal(768, embeddingService.Dimensions);
        Assert.Equal("gemini-embedding-001-768", embeddingService.ModelId);
    }

    [Fact]
    public void Dependency_injection_keeps_feature_hash_embeddings_by_default()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RunbookRetrieval:Mode"] = "pgvector",
                ["ConnectionStrings:Runbooks"] =
                    "Host=127.0.0.1;Port=54329;Database=connectedops;Username=connectedops;Password=connectedops_dev"
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddConnectedOperations(configuration)
            .BuildServiceProvider();

        var embeddingService = provider.GetRequiredService<ITextEmbeddingService>();
        Assert.IsType<FeatureHashEmbeddingService>(embeddingService);
        Assert.Equal("feature-hash-256-v1", embeddingService.ModelId);
    }

    private static GeminiEmbeddingOptions Options(int dimensions)
    {
        return new GeminiEmbeddingOptions(
            "test-key",
            "gemini-embedding-001",
            dimensions,
            new Uri("https://generativelanguage.googleapis.com/v1beta/"));
    }

    private static HttpResponseMessage JsonResponse(object value)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                Encoding.UTF8,
                "application/json")
        };
    }

    private sealed class StubGeminiEmbeddingClient(float[] embedding) : IGeminiEmbeddingClient
    {
        public EmbeddingPurpose LastPurpose { get; private set; }

        public Task<float[]> EmbedAsync(
            string text,
            EmbeddingPurpose purpose,
            CancellationToken cancellationToken)
        {
            LastPurpose = purpose;
            return Task.FromResult(embedding);
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return handler(request);
        }
    }
}
