using System.Linq;
using Adelaide.Core;
using Xunit;

namespace Adelaide.Core.Tests;

public class CatalogVocabTests
{
    [Fact]
    public void Find_resolves_known_content_type() => Assert.NotNull(DocumentCatalog.Find("Verordnung"));

    [Fact]
    public void Find_is_case_insensitive() => Assert.NotNull(DocumentCatalog.Find("verordnung"));

    [Fact]
    public void CareLevel_vocabulary_is_one_to_five() =>
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, Vocab.For("CareLevel"));

    // Drift guard: every Choice column referenced by the catalog must have a vocabulary in Vocab.
    [Fact]
    public void Every_catalog_choice_column_has_a_vocabulary()
    {
        var missing = DocumentCatalog.ChoiceColumns().Where(c => Vocab.For(c) is null).ToList();
        Assert.True(missing.Count == 0, "Choice columns missing from Vocab: " + string.Join(", ", missing));
    }
}
