using ConnectedOps.Application;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectedOps.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddConnectedOperations(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOperationsStore, InMemoryOperationsStore>();
        services.AddSingleton<IToolAuditStore, InMemoryToolAuditStore>();
        services.AddSingleton<IReadOnlyOperationsTool, RecentAssetTelemetryTool>();
        services.AddSingleton<OperationsToolService>();
        AddRunbookRetrieval(services, configuration);
        AddDiagnosticProvider(services, configuration);
        services.AddSingleton<OperationsService>();

        return services;
    }

    private static void AddRunbookRetrieval(
        IServiceCollection services,
        IConfiguration? configuration)
    {
        var mode = configuration?["RunbookRetrieval:Mode"]?.Trim() ?? "local";
        if (mode.Equals("local", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IRunbookRetriever, LocalRunbookRetriever>();
            return;
        }

        if (!mode.Equals("pgvector", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported runbook retrieval mode '{mode}'. Use 'local' or 'pgvector'.");
        }

        var connectionString = configuration?.GetConnectionString("Runbooks");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:Runbooks is required when RunbookRetrieval:Mode is 'pgvector'.");
        }

        AddEmbeddingService(services, configuration);
        services.AddSingleton<IRunbookRetriever>(provider =>
            new PgvectorRunbookRetriever(
                connectionString,
                provider.GetRequiredService<ITextEmbeddingService>()));
    }

    private static void AddEmbeddingService(
        IServiceCollection services,
        IConfiguration? configuration)
    {
        var providerName = configuration?["Embeddings:Provider"]?.Trim() ?? "feature-hash";
        if (providerName.Equals("feature-hash", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ITextEmbeddingService, FeatureHashEmbeddingService>();
            return;
        }

        if (!providerName.Equals("gemini", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported embedding provider '{providerName}'. Use 'feature-hash' or 'gemini'.");
        }

        var apiKey = configuration?["Embeddings:Gemini:ApiKey"]?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            apiKey = configuration?["Diagnostics:Gemini:ApiKey"]?.Trim();
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Embeddings:Gemini:ApiKey is required when Embeddings:Provider is 'gemini'.");
        }

        var model = configuration?["Embeddings:Gemini:Model"]?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gemini-embedding-001";
        }

        var dimensions = 768;
        var dimensionsValue = configuration?["Embeddings:Gemini:Dimensions"]?.Trim();
        if (!string.IsNullOrWhiteSpace(dimensionsValue)
            && (!int.TryParse(dimensionsValue, out dimensions)
                || dimensions is < 128 or > 2_000))
        {
            throw new InvalidOperationException(
                "Embeddings:Gemini:Dimensions must be an integer between 128 and 2000.");
        }

        var endpointValue = configuration?["Embeddings:Gemini:Endpoint"]?.Trim()
            ?? "https://generativelanguage.googleapis.com/v1beta/";
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not "https")
        {
            throw new InvalidOperationException(
                "Embeddings:Gemini:Endpoint must be an absolute HTTPS URL.");
        }

        if (!endpointValue.EndsWith('/'))
        {
            endpoint = new Uri(endpointValue + "/");
        }

        services.AddSingleton(new GeminiEmbeddingOptions(apiKey, model, dimensions, endpoint));
        services.AddHttpClient<IGeminiEmbeddingClient, GeminiEmbeddingClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<ITextEmbeddingService, GeminiEmbeddingService>();
    }

    private static void AddDiagnosticProvider(
        IServiceCollection services,
        IConfiguration? configuration)
    {
        var providerName = configuration?["Diagnostics:Provider"]?.Trim() ?? "mock";
        if (providerName.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IDiagnosticService, MockDiagnosticService>();
            return;
        }

        if (!providerName.Equals("gemini", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported diagnostic provider '{providerName}'. Use 'mock' or 'gemini'.");
        }

        var apiKey = configuration?["Diagnostics:Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Diagnostics:Gemini:ApiKey is required when Diagnostics:Provider is 'gemini'.");
        }

        var model = configuration?["Diagnostics:Gemini:Model"]?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            model = "gemini-3.8-flash";
        }

        var endpointValue = configuration?["Diagnostics:Gemini:Endpoint"]?.Trim()
            ?? "https://generativelanguage.googleapis.com/v1beta/interactions";
        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not "https")
        {
            throw new InvalidOperationException(
                "Diagnostics:Gemini:Endpoint must be an absolute HTTPS URL.");
        }

        services.AddSingleton(new GeminiDiagnosticOptions(apiKey, model, endpoint));
        services.AddHttpClient<IGeminiInteractionsClient, GeminiInteractionsClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IDiagnosticService, GeminiDiagnosticService>();
    }
}
