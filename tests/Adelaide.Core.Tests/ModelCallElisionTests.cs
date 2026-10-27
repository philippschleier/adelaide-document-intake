using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

// The elision helper is what keeps captured request bodies byte-faithful EXCEPT for bulk image payloads, which
// are replaced by content-hash + size markers (capture decision 6). All fixtures are synthetic.
public class ModelCallElisionTests
{
    private static string DataUrlOf(byte[] bytes) => $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";

    [Fact]
    public void An_embedded_image_is_replaced_by_its_hash_and_size()
    {
        var img = Encoding.UTF8.GetBytes(new string('x', 400)); // 400 raw bytes → >200 base64 chars
        var json = JsonSerializer.Serialize(new { messages = new[] { new { image_url = new { url = DataUrlOf(img) } } } });

        var elided = ModelCallElision.ElideDataUrls(json);

        Assert.DoesNotContain(Convert.ToBase64String(img), elided, StringComparison.Ordinal);
        var expectedSha = Convert.ToHexString(SHA256.HashData(img)).ToLowerInvariant();
        Assert.Contains($"elided:image/jpeg;sha256={expectedSha};bytes={img.Length}", elided, StringComparison.Ordinal);
        // Still valid JSON after the in-string replacement.
        using var d = JsonDocument.Parse(elided);
        Assert.Equal(JsonValueKind.Array, d.RootElement.GetProperty("messages").ValueKind);
    }

    [Fact]
    public void Every_page_gets_its_own_marker()
    {
        var a = Encoding.UTF8.GetBytes(new string('a', 300));
        var b = Encoding.UTF8.GetBytes(new string('b', 300));
        var json = $"[\"{DataUrlOf(a)}\",\"{DataUrlOf(b)}\"]";

        var elided = ModelCallElision.ElideDataUrls(json);

        Assert.Contains(ModelCallElision.Sha256Hex(a), elided, StringComparison.Ordinal);
        Assert.Contains(ModelCallElision.Sha256Hex(b), elided, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_text_and_short_data_urls_are_untouched()
    {
        // Below the 200-char base64 floor nothing matches — and plain German document text never does.
        var json = JsonSerializer.Serialize(new { text = "Verordnung häuslicher Krankenpflege data:image/png;base64,AAAA" });
        Assert.Equal(json, ModelCallElision.ElideDataUrls(json));
    }

    [Fact]
    public void The_direct_marker_matches_the_regex_path()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('p', 512));
        var viaRegex = ModelCallElision.ElideDataUrls($"\"{DataUrlOf(bytes)}\"");
        Assert.Contains(ModelCallElision.Marker("image/jpeg", bytes), viaRegex, StringComparison.Ordinal);
    }
}
