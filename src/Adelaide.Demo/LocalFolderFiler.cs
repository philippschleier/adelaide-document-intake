using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Adelaide.Core;
using Adelaide.SharePoint;

namespace Adelaide.Demo;

/// <summary>
/// Demo stand-in for the SharePoint document library (<see cref="SharePointSink"/>): files each PDF into a local folder
/// tree <c>{root}/{site}/{library}/{driveItemId}.pdf</c> with a JSON metadata sidecar holding the content type and
/// column values SharePoint would have received.
///
/// It honours the contract the outbox relies on, not just the happy path: <see cref="FileAsync(FilingSpec, CancellationToken)"/>
/// is an idempotent upsert keyed by the destination + canonical filename (a reconcile re-drive converges on the same
/// item), lookups are BY driveItemId within the recorded target drive, and delete is idempotent (already-gone = success).
/// </summary>
public sealed class LocalFolderFiler(string root) : IFiledDocumentStore, IDocumentSink
{
    /// <summary>The medical default destination, mirroring SharePointSink's "Patient Records" on the configured site.</summary>
    public static readonly FilingTarget MedicalDefault = new("demo-site", "Patient Records", ContentType: null, RequiresPatient: true);

    public string Root { get; } = Path.GetFullPath(root);

    public async Task FileAsync(IncomingDocument doc, DocumentUnit unit, ProcessingResult result, CancellationToken ct = default)
    {
        if (result.Disposition != Disposition.AutoFile) return; // review/dead-letter stay in the intake store + ledger
        await FileAsync(SharePointSink.BuildSpec(doc, unit, result), ct);
    }

    public async Task<FilingResult> FileAsync(FilingSpec spec, CancellationToken ct = default)
    {
        var target = spec.Target ?? MedicalDefault;
        var dir = DriveDir(target);
        Directory.CreateDirectory(dir);

        var pdf = await File.ReadAllBytesAsync(spec.PdfPath, ct);
        var id = DriveItemId(target, spec.FileName); // same destination + name → same item (PUT-replace semantics)
        await File.WriteAllBytesAsync(Path.Combine(dir, id + ".pdf"), pdf, ct);
        DemoCorpus.Write(Path.Combine(dir, id + ".json"), new
        {
            driveItemId = id,
            fileName = spec.FileName,
            contentType = target.ContentType ?? spec.ContentType,
            library = target.Library,
            fields = spec.Fields,
            volltextChars = spec.Volltext.Length,
            filedUtc = DateTimeOffset.UtcNow,
        });
        return new FilingResult(id, PdfAId.Declares(pdf), DiRendition: false);
    }

    public Task<bool> ExistsByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(PdfPath(driveItemId, target)));

    public Task<bool> TryDeleteByIdAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default)
    {
        var pdf = PdfPath(driveItemId, target);
        File.Delete(pdf);                              // no-op when absent: already-gone folds to success, like a Graph 404
        File.Delete(Path.ChangeExtension(pdf, ".json"));
        return Task.FromResult(true);
    }

    public async Task<byte[]?> DownloadFiledPdfAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default)
    {
        var pdf = PdfPath(driveItemId, target);
        return File.Exists(pdf) ? await File.ReadAllBytesAsync(pdf, ct) : null;
    }

    public Task<HttpResponseMessage?> OpenFiledPdfStreamAsync(string driveItemId, FilingTarget? target = null, CancellationToken ct = default)
    {
        var pdf = PdfPath(driveItemId, target);
        if (!File.Exists(pdf)) return Task.FromResult<HttpResponseMessage?>(null);
        var stream = File.OpenRead(pdf);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        content.Headers.ContentLength = stream.Length;
        return Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }

    private string PdfPath(string driveItemId, FilingTarget? target)
    {
        // The id is ours (hex), but it arrives over HTTP via the ledger — refuse anything that could escape the root.
        if (driveItemId.Length == 0 || !driveItemId.All(char.IsAsciiHexDigit))
            throw new ArgumentException("not a demo driveItemId", nameof(driveItemId));
        return Path.Combine(DriveDir(target ?? MedicalDefault), driveItemId + ".pdf");
    }

    private string DriveDir(FilingTarget t) => Path.Combine(Root, Safe(t.SiteId), Safe(t.Library));

    private static string Safe(string s) =>
        string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')).Trim();

    private static string DriveItemId(FilingTarget t, string fileName) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{t.SiteId}|{t.Library}|{fileName}")))[..24].ToLowerInvariant();
}
