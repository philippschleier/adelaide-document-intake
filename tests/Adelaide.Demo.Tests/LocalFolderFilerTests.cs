using Adelaide.SharePoint;

namespace Adelaide.Demo.Tests;

/// <summary>The local stand-in must honour the same contract the outbox relies on from SharePointSink: idempotent
/// upsert (a reconcile re-drive converges on one item), lookups by driveItem id within the recorded drive, and an
/// idempotent delete — otherwise the demo would exercise a friendlier filer than production has.</summary>
public sealed class LocalFolderFilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "adelaide-demo-filer", Guid.NewGuid().ToString("N"));
    private readonly string _pdf;

    public LocalFolderFilerTests()
    {
        Directory.CreateDirectory(_root);
        _pdf = Path.Combine(_root, "in.pdf");
        File.WriteAllBytes(_pdf, "%PDF-1.4\n%synthetic\n"u8.ToArray());
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private FilingSpec Spec(string name, FilingTarget? target = null) =>
        new(name, "Verordnung", new Dictionary<string, object?> { ["PatientLookupId"] = 101 }, "volltext", _pdf, target);

    [Fact]
    public async Task Filing_the_same_spec_twice_converges_on_one_item()
    {
        var filer = new LocalFolderFiler(Path.Combine(_root, "filed"));
        var a = await filer.FileAsync(Spec("2026-09-28_Verordnung_Z000000005.pdf"));
        var b = await filer.FileAsync(Spec("2026-09-28_Verordnung_Z000000005.pdf"));
        Assert.Equal(a.DriveItemId, b.DriveItemId);
        Assert.False(a.DiRendition); // no Document Intelligence in demo mode — the original is filed
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "filed"), "*.pdf", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Lookups_are_by_id_within_the_recorded_drive_and_delete_is_idempotent()
    {
        var filer = new LocalFolderFiler(Path.Combine(_root, "filed"));
        var business = new FilingTarget("demo-site", "Business – Eingang", "Document", RequiresPatient: false);
        var id = (await filer.FileAsync(Spec("2026-10-12_Document_x.pdf", business))).DriveItemId;

        Assert.True(await filer.ExistsByIdAsync(id, business));
        Assert.False(await filer.ExistsByIdAsync(id));                // the medical default drive does not hold it
        Assert.NotNull(await filer.DownloadFiledPdfAsync(id, business));
        using (var stream = await filer.OpenFiledPdfStreamAsync(id, business))
            Assert.Equal("application/pdf", stream!.Content.Headers.ContentType!.MediaType);

        Assert.True(await filer.TryDeleteByIdAsync(id, business));
        Assert.True(await filer.TryDeleteByIdAsync(id, business));    // already gone folds to success
        Assert.Null(await filer.DownloadFiledPdfAsync(id, business));
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("")]
    [InlineData("abc/def")]
    public async Task A_driveItem_id_that_is_not_ours_is_refused(string id)
    {
        var filer = new LocalFolderFiler(Path.Combine(_root, "filed"));
        await Assert.ThrowsAsync<ArgumentException>(() => filer.DownloadFiledPdfAsync(id));
    }
}
