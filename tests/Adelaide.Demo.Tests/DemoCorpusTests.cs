using Adelaide.Core;
using Adelaide.Demo;
using Adelaide.SharePoint;

namespace Adelaide.Demo.Tests;

/// <summary>Pins the synthetic demo corpus to the behaviour it is documented to show (demo/README.md): each sample is
/// run through the demo transcriber + extractor + patient directory and the REAL verify-or-review gate, and must land
/// in the documented disposition for the documented reason. If a gate rule changes, this is where the demo's story
/// is caught going stale.</summary>
public sealed class DemoCorpusTests
{
    private static readonly string DemoDir = FindDemoDir();
    private static readonly DemoCorpus Corpus = DemoCorpus.Load(DemoDir);
    private static readonly PipelineOptions Opt = new(Today: new DateOnly(2026, 10, 27)); // fixed clock: the date-validity guard must not drift

    [Fact]
    public void Every_fixture_is_keyed_to_the_committed_pdf_bytes()
    {
        Assert.NotEmpty(Corpus.Documents);
        foreach (var d in Corpus.Documents)
            Assert.Equal(d.Sha256, DemoCorpus.Sha256Hex(File.ReadAllBytes(Path.Combine(DemoDir, "samples", d.File))));
    }

    [Fact]
    public void Every_page_is_labelled_synthetic_and_every_patient_kvnr_is_check_digit_valid()
    {
        Assert.All(Corpus.Documents.SelectMany(d => d.Pages), p => Assert.StartsWith("SYNTHETISCHES MUSTERDOKUMENT", p, StringComparison.Ordinal));
        Assert.All(Corpus.Patients, p => Assert.True(Kvnr.IsValid(p.Kvnr), p.Kvnr));
    }

    [Theory]
    [InlineData("01-verordnung-mustermann.pdf", Disposition.AutoFile, "kvnr", MatchOutcome.Single, new string[0])]
    [InlineData("02-genehmigung-beispiel.pdf", Disposition.AutoFile, "kvnr", MatchOutcome.Single, new string[0])]
    [InlineData("04-arztbrief-musterfrau.pdf", Disposition.Review, "name+dob", MatchOutcome.Single, new[] { ReasonCodes.NotAllowlisted })]
    [InlineData("05-arztbrief-testfall-namensgleich.pdf", Disposition.Review, "name+dob", MatchOutcome.Multiple, new[] { ReasonCodes.AmbiguousNameDob })]
    [InlineData("06-verordnung-kvnr-pruefziffer.pdf", Disposition.Review, "name+dob", MatchOutcome.Single, new[] { ReasonCodes.KvnrCheckDigit })]
    [InlineData("07-rechnung-buerobedarf.pdf", Disposition.Review, "none", MatchOutcome.None, new[] { ReasonCodes.BusinessDomain })]
    [InlineData("08-unleserliches-fax.pdf", Disposition.Review, "none", MatchOutcome.None, new[] { ReasonCodes.EmptyTranscript, ReasonCodes.LowQuality })]
    [InlineData("09-stapel-zwei-dokumente.pdf", Disposition.Review, "name+dob", MatchOutcome.Single, new[] { ReasonCodes.UnclassifiedType, ReasonCodes.LowConfidence })]
    public async Task Each_sample_lands_where_the_demo_says_it_does(string file, Disposition expected, string method, MatchOutcome outcome, string[] codes)
    {
        var (result, _) = await RunAsync(file);
        Assert.Equal(expected, result.Disposition);
        Assert.Equal(method, result.Match.Method);
        Assert.Equal(outcome, result.Match.Outcome);
        foreach (var c in codes) Assert.Contains(c, result.ReasonCodes ?? []);
    }

    [Fact]
    public async Task Ambiguous_name_and_birth_date_offers_both_people_and_guesses_neither()
    {
        var (result, _) = await RunAsync("05-arztbrief-testfall-namensgleich.pdf");
        Assert.Null(result.Match.Patient);
        Assert.Equal(new[] { 104, 105 }, result.Match.Candidates!.Select(p => p.ListItemId).Order());
    }

    [Fact]
    public async Task An_invalid_kvnr_is_never_looked_up_and_name_dob_picks_the_single_active_record()
    {
        var (result, data) = await RunAsync("06-verordnung-kvnr-pruefziffer.pdf");
        Assert.False(Kvnr.IsValid(data.Value("KVNR")));
        Assert.Equal(106, result.Match.Patient!.ListItemId); // 107 is the same name+DOB but Inaktiv
    }

    [Fact]
    public async Task The_fax_copy_shares_the_business_duplicate_key_of_the_original_approval()
    {
        var (original, _) = await RunAsync("02-genehmigung-beispiel.pdf");
        var (fax, _) = await RunAsync("03-genehmigung-beispiel-fax-kopie.pdf");
        Assert.NotEqual(Corpus.Documents.Single(d => d.File.StartsWith("02", StringComparison.Ordinal)).Sha256,
                        Corpus.Documents.Single(d => d.File.StartsWith("03", StringComparison.Ordinal)).Sha256); // different bytes …
        var key = SharePointSink.BuildDuplicateKeyPrefix(original, "Genehmigung");
        Assert.NotNull(key);
        Assert.Equal(key, SharePointSink.BuildDuplicateKeyPrefix(fax, "Genehmigung"));                  // … same business key
    }

    [Fact]
    public async Task A_reviewer_split_of_the_bundle_gives_each_child_its_own_extraction()
    {
        var bundle = Corpus.Documents.Single(d => d.File.StartsWith("09", StringComparison.Ordinal));
        var directory = new DemoPatientDirectory(Corpus.Patients);
        var extractor = new DemoExtractor(Corpus);
        var outcomes = new List<(Disposition, string?, int?)>();
        foreach (var page in bundle.Pages)
        {
            // A split child reuses its slice of the parent transcript (Program.cs joins carried pages the same way).
            var t = new Transcript(DemoCorpus.JoinPages([page]), 1, bundle.Legibility, [], [page]);
            var unit = new DocumentUnit("child", 1, 1, t);
            var data = await extractor.ExtractAsync(unit);
            var match = await directory.MatchAsync(data);
            var r = IntakePipeline.Decide(unit, t, data, match, Opt);
            outcomes.Add((r.Disposition, data.Value("ContentType"), match.Patient?.ListItemId));
        }
        Assert.Equal((Disposition.AutoFile, "Verordnung", 102), outcomes[0]);
        Assert.Equal((Disposition.Review, "Arztbrief", 101), outcomes[1]);
    }

    [Fact]
    public async Task Patient_search_mirrors_the_graph_adapter()
    {
        var dir = new DemoPatientDirectory(Corpus.Patients);
        Assert.Empty(await dir.SearchAsync("M"));                                        // < 2 chars → nothing
        Assert.Equal(2, (await dir.SearchAsync("muster")).Count);                        // surname prefix, case-insensitive
        Assert.Equal(101, Assert.Single(await dir.SearchAsync("z000000005")).ListItemId); // full KVNR → exact
    }

    private static async Task<(ProcessingResult Result, ExtractedData Data)> RunAsync(string file)
    {
        var path = Path.Combine(DemoDir, "samples", file);
        var t = await new DemoTranscriber(Corpus).TranscribeAsync(new IncomingDocument(file, path, SourceChannel.Api, file));
        var unit = new DocumentUnit(file, 1, t.PageCount, t, path);
        var data = await new DemoExtractor(Corpus).ExtractAsync(unit);
        var match = await new DemoPatientDirectory(Corpus.Patients).MatchAsync(data);
        return (IntakePipeline.Decide(unit, t, data, match, Opt), data);
    }

    internal static string FindDemoDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "demo", "fixtures.json"))) return Path.Combine(dir.FullName, "demo");
        throw new DirectoryNotFoundException("demo/fixtures.json not found above the test output directory");
    }
}
