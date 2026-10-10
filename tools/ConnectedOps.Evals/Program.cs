using System.Text.Json;
using System.Text.Json.Serialization;
using ConnectedOps.Application;
using ConnectedOps.Domain;
using ConnectedOps.Evals;
using ConnectedOps.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    var datasetPath = ReadOption(args, "--dataset") ?? "evals/cases.json";
    var outputPath = ReadOption(args, "--output") ?? "artifacts/eval-report.json";
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    EvaluationDataset dataset;
    try
    {
        var datasetJson = await File.ReadAllTextAsync(datasetPath);
        dataset = JsonSerializer.Deserialize<EvaluationDataset>(datasetJson, jsonOptions)
            ?? throw new InvalidOperationException("Evaluation dataset is empty.");
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Could not load evaluation dataset '{datasetPath}': {exception.Message}");
        return 2;
    }

    if (dataset.Cases.Count is 0)
    {
        Console.Error.WriteLine("Evaluation dataset contains no cases.");
        return 2;
    }

    var configuration = new ConfigurationBuilder()
        .AddEnvironmentVariables()
        .Build();
    await using var serviceProvider = new ServiceCollection()
        .AddConnectedOperations(configuration)
        .BuildServiceProvider();
    var diagnosticService = serviceProvider.GetRequiredService<IDiagnosticService>();
    var configuredProvider = configuration["Diagnostics:Provider"]?.Trim() ?? "mock";
    var now = DateTimeOffset.UtcNow;
    var results = new List<EvaluationCaseResult>();

    foreach (var evaluationCase in dataset.Cases)
    {
        try
        {
            var (incident, asset, events) = BuildContext(evaluationCase, now);
            var (analysis, action) = await diagnosticService.AnalyzeAsync(
                incident,
                asset,
                events,
                CancellationToken.None);
            results.Add(evaluationCase.Expectations.ExpectRefusal
                ? EvaluationScorer.UnexpectedDiagnosis(evaluationCase, analysis)
                : EvaluationScorer.Score(
                    evaluationCase,
                    analysis,
                    action));
        }
        catch (Exception exception)
        {
            results.Add(evaluationCase.Expectations.ExpectRefusal
                ? EvaluationScorer.ScoreRefusal(
                    evaluationCase,
                    configuredProvider,
                    exception)
                : EvaluationScorer.ProviderFailure(
                    evaluationCase,
                    configuredProvider,
                    exception));
        }
    }

    var reportProvider = results
        .GroupBy(result => result.Provider, StringComparer.Ordinal)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.Ordinal)
        .First()
        .Key;
    var report = new EvaluationReport(
        dataset.Version,
        now,
        reportProvider,
        decimal.Round(results.Average(result => result.Score), 1),
        results.All(result => result.Passed),
        results);

    var outputDirectory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(outputDirectory))
    {
        Directory.CreateDirectory(outputDirectory);
    }

    await File.WriteAllTextAsync(
        outputPath,
        JsonSerializer.Serialize(report, jsonOptions));
    PrintReport(report, outputPath);
    return report.Passed ? 0 : 1;
}

static (Incident Incident, ConnectedAsset Asset, TelemetryEvent[] Events) BuildContext(
    EvaluationCase evaluationCase,
    DateTimeOffset now)
{
    var assetId = Guid.NewGuid();
    var asset = new ConnectedAsset
    {
        Id = assetId,
        Name = evaluationCase.Asset.Name,
        Type = evaluationCase.Asset.Type,
        Location = evaluationCase.Asset.Location
    };
    asset.MarkOffline(now);
    var incident = new Incident
    {
        Id = Guid.NewGuid(),
        AssetId = assetId,
        Title = evaluationCase.Incident.Title,
        Severity = evaluationCase.Incident.Severity,
        OpenedAt = now
    };
    var events = evaluationCase.Telemetry
        .Select((item, index) => new TelemetryEvent(
            Guid.NewGuid(),
            assetId,
            now.AddMinutes(index - evaluationCase.Telemetry.Count),
            item.EventType,
            item.Message,
            item.LatencyMs))
        .ToArray();
    return (incident, asset, events);
}

static void PrintReport(EvaluationReport report, string outputPath)
{
    Console.WriteLine($"ConnectedOps evaluation dataset {report.DatasetVersion}");
    Console.WriteLine($"Provider: {report.Provider}");
    Console.WriteLine();
    Console.WriteLine("| Case | Score | Result |");
    Console.WriteLine("| --- | ---: | :---: |");
    foreach (var result in report.Cases)
    {
        Console.WriteLine(
            $"| {result.Name} | {result.Score:0.0}% | {(result.Passed ? "PASS" : "FAIL")} |");
    }

    var failedChecks = report.Cases
        .SelectMany(result => result.Checks
            .Where(check => !check.Passed)
            .Select(check => (result.Name, Check: check)))
        .ToArray();
    if (failedChecks.Length > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Failed checks:");
        foreach (var failed in failedChecks)
        {
            Console.WriteLine(
                $"- {failed.Name} / {failed.Check.Name}: {failed.Check.Detail}");
        }
    }

    Console.WriteLine();
    Console.WriteLine(
        $"Overall: {report.Score:0.0}% — {(report.Passed ? "PASS" : "FAIL")}");
    Console.WriteLine($"JSON report: {outputPath}");
}

static string? ReadOption(string[] args, string name)
{
    var index = Array.IndexOf(args, name);
    if (index < 0)
    {
        return null;
    }

    if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
    {
        throw new ArgumentException($"Option '{name}' requires a value.");
    }

    return args[index + 1];
}
