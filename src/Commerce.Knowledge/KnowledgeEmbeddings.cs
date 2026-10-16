using System.Net.Http.Json;
using System.Text.Json;

namespace Commerce.Knowledge;

public interface IKnowledgeEmbeddings
{
    Task<string> IdentityAsync(CancellationToken ct);
    Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct);
}

public sealed class KnowledgeEmbeddings(HttpClient http, string model = "embeddinggemma:latest") : IKnowledgeEmbeddings
{
    public const int Dimensions = 768;
    public async Task<string> IdentityAsync(CancellationToken ct)
    {
        ValidateHost();
        using var response = await http.GetAsync("api/tags", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var data = await ReadAsync(response, 262144, ct);
        var found = data.GetProperty("models").EnumerateArray().FirstOrDefault(m => m.GetProperty("name").GetString() == model);
        if (found.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException($"Install the configured embedding model: {model}.");
        var digest = found.GetProperty("digest").GetString()!;
        if (digest.Length != 64 || !digest.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid embedding model digest.");
        return model + "@" + digest;
    }
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> inputs, CancellationToken ct)
    {
        ValidateHost();
        if (inputs.Count is < 1 or > 16 || inputs.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 6000))
            throw new InvalidDataException("Invalid embedding batch.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/embed") { Content = JsonContent.Create(new { model, input = inputs, truncate = false, dimensions = Dimensions, keep_alive = "10m" }) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var data = await ReadAsync(response, 1_048_576, ct);
        var vectors = data.GetProperty("embeddings").Deserialize<float[][]>() ?? throw new InvalidDataException("Empty embeddings.");
        if (vectors.Length != inputs.Count || vectors.Any(v => v.Length != Dimensions || v.Any(x => !float.IsFinite(x)) || v.All(x => x == 0)))
            throw new InvalidDataException("Invalid embedding dimensions or values.");
        return vectors;
    }
    private void ValidateHost()
    {
        if (http.BaseAddress is not { Scheme: "http" or "https" } address || (!address.IsLoopback && address.Host != "host.docker.internal"))
            throw new InvalidOperationException("The embedding service must be local or on the Docker host gateway.");
    }
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, int limit, CancellationToken ct)
    {
        await using var source = await response.Content.ReadAsStreamAsync(ct); using var buffer = new MemoryStream();
        var chunk = new byte[4096]; int count;
        while ((count = await source.ReadAsync(chunk, ct)) > 0)
        { if (buffer.Length + count > limit) throw new InvalidDataException("Embedding response exceeded its size limit."); buffer.Write(chunk, 0, count); }
        return JsonSerializer.Deserialize<JsonElement>(buffer.ToArray());
    }
}
