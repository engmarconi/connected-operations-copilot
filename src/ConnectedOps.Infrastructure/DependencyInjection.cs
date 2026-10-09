using ConnectedOps.Application;
using Microsoft.Extensions.DependencyInjection;

namespace ConnectedOps.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddConnectedOperations(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IOperationsStore, InMemoryOperationsStore>();
        services.AddSingleton<IRunbookRetriever, LocalRunbookRetriever>();
        services.AddSingleton<IDiagnosticService, MockDiagnosticService>();
        services.AddSingleton<OperationsService>();

        return services;
    }
}
