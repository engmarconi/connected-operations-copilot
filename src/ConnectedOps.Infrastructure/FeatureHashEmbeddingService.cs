using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ConnectedOps.Application;

namespace ConnectedOps.Infrastructure;

public sealed partial class FeatureHashEmbeddingService : ITextEmbeddingService
{
    public int Dimensions => 256;
    public string ModelId => "feature-hash-256-v1";

    public Task<float[]> EmbedAsync(
        string text,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var tokens = TokenPattern()
            .Matches(text.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(token => token.Length > 1)
            .ToArray();
        if (tokens.Length is 0)
        {
            throw new ArgumentException("Text must contain at least one searchable token.", nameof(text));
        }

        var embedding = new float[Dimensions];
        foreach (var token in tokens)
        {
            AddFeature(embedding, token, weight: 1f);
        }

        for (var index = 0; index < tokens.Length - 1; index++)
        {
            AddFeature(
                embedding,
                $"{tokens[index]}::{tokens[index + 1]}",
                weight: 1.25f);
        }

        var magnitude = MathF.Sqrt(embedding.Sum(value => value * value));
        if (magnitude > 0)
        {
            for (var index = 0; index < embedding.Length; index++)
            {
                embedding[index] /= magnitude;
            }
        }

        return Task.FromResult(embedding);
    }

    private static void AddFeature(float[] embedding, string feature, float weight)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(feature));
        var index = (int)(BinaryPrimitives.ReadUInt32LittleEndian(hash) % embedding.Length);
        var sign = (hash[4] & 1) is 0 ? 1f : -1f;
        embedding[index] += sign * weight;
    }

    [GeneratedRegex("[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
