using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ConnectedOps.Application;
using ConnectedOps.Domain;

namespace ConnectedOps.Infrastructure;

public sealed partial class LocalRunbookRetriever : IRunbookRetriever
{
    private static readonly HashSet<string> StopWords =
    [
        "a", "an", "and", "are", "at", "by", "for", "from", "has", "in",
        "is", "of", "on", "or", "the", "to", "was", "with"
    ];

    private readonly IReadOnlyList<RunbookChunk> _chunks;

    public LocalRunbookRetriever()
        : this(LoadEmbeddedRunbooks())
    {
    }

    internal LocalRunbookRetriever(IEnumerable<RunbookChunk> chunks)
    {
        _chunks = chunks.ToArray();
        if (_chunks.Count is 0)
        {
            throw new InvalidOperationException("At least one runbook chunk is required.");
        }
    }

    public IReadOnlyList<RunbookChunk> Chunks => _chunks;

    public Task<IReadOnlyList<RunbookChunk>> RetrieveAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        var queryTerms = Tokenize(query);
        var results = _chunks
            .Select(chunk => new ScoredChunk(chunk, Score(chunk, queryTerms)))
            .Where(result => result.Score > 0)
            .GroupBy(result => result.Chunk.DocumentId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.Chunk.Section, StringComparer.Ordinal)
                .First())
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Chunk.DocumentId, StringComparer.Ordinal)
            .Take(maxResults)
            .Select(result => result.Chunk)
            .ToArray();

        return Task.FromResult<IReadOnlyList<RunbookChunk>>(results);
    }

    private static int Score(RunbookChunk chunk, IReadOnlySet<string> queryTerms)
    {
        var titleTerms = Tokenize(chunk.Title);
        var sectionTerms = Tokenize(chunk.Section);
        var contentTerms = Tokenize(chunk.Content);
        var keywordTerms = chunk.Keywords
            .SelectMany(Tokenize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return queryTerms.Sum(term =>
            (keywordTerms.Contains(term) ? 6 : 0)
            + (titleTerms.Contains(term) ? 4 : 0)
            + (sectionTerms.Contains(term) ? 2 : 0)
            + (contentTerms.Contains(term) ? 1 : 0));
    }

    private static HashSet<string> Tokenize(string value)
    {
        return TokenPattern()
            .Matches(value.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(token => token.Length > 1 && !StopWords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<RunbookChunk> LoadEmbeddedRunbooks()
    {
        var assembly = typeof(LocalRunbookRetriever).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Runbooks.", StringComparison.Ordinal)
                && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var chunks = new List<RunbookChunk>();
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Embedded runbook '{resource}' could not be read.");
            using var reader = new StreamReader(stream);
            chunks.AddRange(Parse(reader.ReadToEnd(), resource));
        }

        return chunks;
    }

    private static IEnumerable<RunbookChunk> Parse(string markdown, string sourceName)
    {
        string? documentId = null;
        string? title = null;
        string? section = null;
        var keywords = Array.Empty<string>();
        var content = new StringBuilder();
        var chunks = new List<RunbookChunk>();

        void FlushSection()
        {
            if (section is null || content.Length is 0)
            {
                return;
            }

            chunks.Add(new RunbookChunk(
                documentId ?? throw InvalidRunbook(sourceName, "Document ID"),
                title ?? throw InvalidRunbook(sourceName, "title"),
                section,
                content.ToString().Trim(),
                keywords));
            content.Clear();
        }

        foreach (var rawLine in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                title = line[2..].Trim();
            }
            else if (line.StartsWith("Document ID:", StringComparison.OrdinalIgnoreCase))
            {
                documentId = line["Document ID:".Length..].Trim();
            }
            else if (line.StartsWith("Keywords:", StringComparison.OrdinalIgnoreCase))
            {
                keywords = line["Keywords:".Length..]
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushSection();
                section = line[3..].Trim();
            }
            else if (section is not null && line.Length > 0)
            {
                if (content.Length > 0)
                {
                    content.Append(' ');
                }

                content.Append(line);
            }
        }

        FlushSection();
        if (chunks.Count is 0)
        {
            throw InvalidRunbook(sourceName, "section content");
        }

        return chunks;
    }

    private static InvalidOperationException InvalidRunbook(string sourceName, string missingValue)
    {
        return new InvalidOperationException(
            $"Runbook '{sourceName}' is missing required {missingValue} metadata.");
    }

    private sealed record ScoredChunk(RunbookChunk Chunk, int Score);

    [GeneratedRegex("[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
