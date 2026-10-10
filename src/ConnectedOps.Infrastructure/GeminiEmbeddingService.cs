using System.Net.Http.Json;
using System.Text.Json;
using ConnectedOps.Application;

namespace ConnectedOps.Infrastructure;

public sealed record GeminiEmbeddingOptions(
    string ApiKey,
    string Model,
    int Dimensions,
    Uri Endpoint);

public interface IGeminiEmbeddingClient
{
    Task<float[]> EmbedAsync(
        string text,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken);
}

public sealed class GeminiEmbeddingClient(
    HttpClient httpClient,
    GeminiEmbeddingOptions options) : IGeminiEmbeddingClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public async Task<float[]> EmbedAsync(
        string text,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var requestUri = new Uri(
            options.Endpoint,
            $"models/{options.Model}:embedContent");
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Add("x-goog-api-key", options.ApiKey);
        request.Content = JsonContent.Create(
            new
            {
                model = $"models/{options.Model}",
                content = new
                {
                    parts = new[]
                    {
                        new { text }
                    }
                },
                taskType = purpose switch
                {
                    EmbeddingPurpose.Document => "RETRIEVAL_DOCUMENT",
                    EmbeddingPurpose.Query => "RETRIEVAL_QUERY",
                    _ => throw new ArgumentOutOfRangeException(nameof(purpose), purpose, null)
                },
                outputDimensionality = options.Dimensions
            },
            options: JsonOptions);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Gemini embedding request failed with {(int)response.StatusCode}: {Truncate(payload)}");
        }

        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("embedding", out var embeddingElement)
            || !embeddingElement.TryGetProperty("values", out var valuesElement)
            || valuesElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                "Gemini embedding response did not include embedding.values.");
        }

        var values = valuesElement.EnumerateArray()
            .Select(value => value.GetSingle())
            .ToArray();
        if (values.Length != options.Dimensions)
        {
            throw new InvalidOperationException(
                $"Gemini embedding returned {values.Length} dimensions; expected {options.Dimensions}.");
        }

        return Normalize(values);
    }

    private static float[] Normalize(float[] values)
    {
        var magnitude = MathF.Sqrt(values.Sum(value => value * value));
        if (magnitude <= 0)
        {
            throw new InvalidOperationException(
                "Gemini embedding returned a zero-magnitude vector.");
        }

        for (var index = 0; index < values.Length; index++)
        {
            values[index] /= magnitude;
        }

        return values;
    }

    private static string Truncate(string value)
    {
        const int maxLength = 400;
        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }
}

public sealed class GeminiEmbeddingService(
    IGeminiEmbeddingClient client,
    GeminiEmbeddingOptions options) : ITextEmbeddingService
{
    public int Dimensions => options.Dimensions;
    public string ModelId => $"{options.Model}-{options.Dimensions}";

    public Task<float[]> EmbedAsync(
        string text,
        EmbeddingPurpose purpose,
        CancellationToken cancellationToken)
    {
        return client.EmbedAsync(text, purpose, cancellationToken);
    }
}
