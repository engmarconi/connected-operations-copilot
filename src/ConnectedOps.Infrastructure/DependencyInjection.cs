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
        AddRunbookRetrieval(services, configuration);
        services.AddSingleton<IDiagnosticService, MockDiagnosticService>();
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

        services.AddSingleton<ITextEmbeddingService, FeatureHashEmbeddingService>();
        services.AddSingleton<IRunbookRetriever>(provider =>
            new PgvectorRunbookRetriever(
                connectionString,
                provider.GetRequiredService<ITextEmbeddingService>()));
    }
}
