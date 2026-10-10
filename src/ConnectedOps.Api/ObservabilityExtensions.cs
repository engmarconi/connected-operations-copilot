using ConnectedOps.Application;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ConnectedOps.Api;

internal static class ObservabilityExtensions
{
    public static WebApplicationBuilder AddConnectedOperationsObservability(
        this WebApplicationBuilder builder)
    {
        var otlpEndpoint = ResolveOtlpEndpoint(builder.Configuration);
        var enableConsoleExporters = builder.Configuration.GetValue(
            "OpenTelemetry:ConsoleExporters",
            defaultValue: builder.Environment.IsDevelopment());

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "O";
            options.UseUtcTimestamp = true;
        });
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.ParseStateValues = true;
            if (enableConsoleExporters)
            {
                logging.AddConsoleExporter();
            }

            if (otlpEndpoint is not null)
            {
                logging.AddOtlpExporter(options =>
                {
                    options.Endpoint = otlpEndpoint;
                });
            }
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(
                    serviceName: OperationsTelemetry.ServiceName,
                    serviceVersion: typeof(ObservabilityExtensions).Assembly
                        .GetName()
                        .Version?
                        .ToString() ?? "0.0.0")
                .AddAttributes(
                [
                    new KeyValuePair<string, object>(
                        "deployment.environment",
                        builder.Environment.EnvironmentName)
                ]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(OperationsTelemetry.ActivitySourceName)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health");
                    })
                    .AddHttpClientInstrumentation();

                if (enableConsoleExporters)
                {
                    tracing.AddConsoleExporter();
                }

                if (otlpEndpoint is not null)
                {
                    tracing.AddOtlpExporter(options =>
                    {
                        options.Endpoint = otlpEndpoint;
                        options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(OperationsTelemetry.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (enableConsoleExporters)
                {
                    metrics.AddConsoleExporter();
                }

                if (otlpEndpoint is not null)
                {
                    metrics.AddOtlpExporter(options =>
                    {
                        options.Endpoint = otlpEndpoint;
                        options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    });
                }
            });

        return builder;
    }

    private static Uri? ResolveOtlpEndpoint(IConfiguration configuration)
    {
        var configured = configuration["OpenTelemetry:Otlp:Endpoint"]
            ?? configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "OpenTelemetry:Otlp:Endpoint must be an absolute http or https URL.");
        }

        return endpoint;
    }
}
