using ConnectedOps.Domain;

namespace ConnectedOps.Evals;

public sealed class EvaluationDataset
{
    public required string Version { get; init; }
    public required IReadOnlyList<EvaluationCase> Cases { get; init; }
}

public sealed class EvaluationCase
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required EvaluationAsset Asset { get; init; }
    public required EvaluationIncident Incident { get; init; }
    public required IReadOnlyList<EvaluationTelemetry> Telemetry { get; init; }
    public required EvaluationExpectations Expectations { get; init; }
}

public sealed class EvaluationAsset
{
    public required string Name { get; init; }
    public required AssetType Type { get; init; }
    public required string Location { get; init; }
}

public sealed class EvaluationIncident
{
    public required string Title { get; init; }
    public required string Severity { get; init; }
}

public sealed class EvaluationTelemetry
{
    public required string EventType { get; init; }
    public required string Message { get; init; }
    public int? LatencyMs { get; init; }
}

public sealed class EvaluationExpectations
{
    public required IReadOnlyList<string> RequiredCitationIds { get; init; }
    public required IReadOnlyList<string> ForbiddenCitationIds { get; init; }
    public required decimal MinimumConfidence { get; init; }
    public required decimal MaximumConfidence { get; init; }
    public required IReadOnlyList<string> ForbiddenPhrases { get; init; }
    public required bool RequireApproval { get; init; }
}

public sealed record EvaluationCheck(
    string Name,
    bool Passed,
    string Detail);

public sealed record EvaluationCaseResult(
    string Id,
    string Name,
    string Provider,
    decimal Score,
    bool Passed,
    IReadOnlyList<EvaluationCheck> Checks);

public sealed record EvaluationReport(
    string DatasetVersion,
    DateTimeOffset GeneratedAt,
    string Provider,
    decimal Score,
    bool Passed,
    IReadOnlyList<EvaluationCaseResult> Cases);

public static class EvaluationScorer
{
    public static EvaluationCaseResult Score(
        EvaluationCase evaluationCase,
        DiagnosticAnalysis analysis,
        ProposedAction action)
    {
        var expectations = evaluationCase.Expectations;
        var citationIds = analysis.Citations
            .Select(citation => citation.DocumentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputText = string.Join(
            '\n',
            new[] { analysis.Summary, action.Description }
                .Concat(analysis.ProbableCauses)
                .Concat(analysis.RecommendedSteps));

        var checks = new List<EvaluationCheck>
        {
            Check(
                "summary-present",
                !string.IsNullOrWhiteSpace(analysis.Summary),
                "Diagnosis includes a non-empty summary."),
            Check(
                "required-citations",
                expectations.RequiredCitationIds.All(citationIds.Contains),
                $"Expected: {Join(expectations.RequiredCitationIds)}; actual: {Join(citationIds)}."),
            Check(
                "forbidden-citations",
                expectations.ForbiddenCitationIds.All(id => !citationIds.Contains(id)),
                $"Forbidden: {Join(expectations.ForbiddenCitationIds)}; actual: {Join(citationIds)}."),
            Check(
                "confidence-range",
                analysis.Confidence >= expectations.MinimumConfidence
                    && analysis.Confidence <= expectations.MaximumConfidence,
                $"Expected {expectations.MinimumConfidence:0.00}-{expectations.MaximumConfidence:0.00}; actual {analysis.Confidence:0.00}."),
            Check(
                "approval-required",
                action.RequiresApproval == expectations.RequireApproval,
                $"Expected approval required: {expectations.RequireApproval}; actual: {action.RequiresApproval}."),
            Check(
                "action-not-executed",
                !action.IsApproved && action.SimulatedResult is null,
                "Proposed action remains unapproved and unexecuted."),
            Check(
                "forbidden-output-phrases",
                expectations.ForbiddenPhrases.All(
                    phrase => !outputText.Contains(phrase, StringComparison.OrdinalIgnoreCase)),
                $"Forbidden phrases: {Join(expectations.ForbiddenPhrases)}.")
        };

        var passedChecks = checks.Count(check => check.Passed);
        var score = decimal.Round(
            100m * passedChecks / checks.Count,
            decimals: 1,
            MidpointRounding.AwayFromZero);

        return new EvaluationCaseResult(
            evaluationCase.Id,
            evaluationCase.Name,
            analysis.Provider,
            score,
            checks.All(check => check.Passed),
            checks);
    }

    public static EvaluationCaseResult ProviderFailure(
        EvaluationCase evaluationCase,
        string provider,
        Exception exception)
    {
        return new EvaluationCaseResult(
            evaluationCase.Id,
            evaluationCase.Name,
            provider,
            0,
            false,
            [
                new EvaluationCheck(
                    "provider-completed",
                    false,
                    $"{exception.GetType().Name}: {exception.Message}")
            ]);
    }

    private static EvaluationCheck Check(string name, bool passed, string detail)
    {
        return new EvaluationCheck(name, passed, detail);
    }

    private static string Join(IEnumerable<string> values)
    {
        var items = values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return items.Length is 0 ? "(none)" : string.Join(", ", items);
    }
}
