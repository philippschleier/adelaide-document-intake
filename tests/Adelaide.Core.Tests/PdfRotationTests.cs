using Adelaide.Ai;
using PdfSharp.Pdf;
using Xunit;

namespace Adelaide.Core.Tests;

/// <summary>
/// Phase-1 rotation core: lossless per-page /Rotate is ABSOLUTE (idempotent — no double-rotate on re-submit),
/// non-destructively reconstructible (zero the /Rotate → original orientation), and fail-safe on a PDF PdfSharp
/// cannot parse (throws BEFORE any byte change so the caller leaves the original untouched). Acceptance criteria 4–6.
/// </summary>
public class PdfRotationTests
{
    private static byte[] MakePdf(int pages = 1)
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < pages; i++) doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Applies_absolute_rotation_per_page_and_reads_it_back()
    {
        var rotated = PdfRotation.WithRotations(MakePdf(2), new Dictionary<int, int> { [1] = 180, [2] = 90 });
        var r = PdfRotation.ReadRotations(rotated);
        Assert.Equal(180, r[1]);
        Assert.Equal(90, r[2]);
    }

    [Fact]
    public void Re_applying_the_same_absolute_target_is_idempotent_no_double_rotate()
    {
        var once = PdfRotation.WithRotations(MakePdf(), new Dictionary<int, int> { [1] = 180 });
        var twice = PdfRotation.WithRotations(once, new Dictionary<int, int> { [1] = 180 });
        Assert.Equal(180, PdfRotation.ReadRotations(once)[1]);
        Assert.Equal(180, PdfRotation.ReadRotations(twice)[1]); // absolute → still 180, NOT 360/0
    }

    [Fact]
    public void Original_orientation_is_reconstructible_by_zeroing_rotate()
    {
        var pdf = MakePdf();
        Assert.Equal(0, PdfRotation.ReadRotations(pdf)[1]);
        var rotated = PdfRotation.WithRotations(pdf, new Dictionary<int, int> { [1] = 270 });
        Assert.Equal(270, PdfRotation.ReadRotations(rotated)[1]);
        var restored = PdfRotation.WithRotations(rotated, new Dictionary<int, int> { [1] = 0 });
        Assert.Equal(0, PdfRotation.ReadRotations(restored)[1]); // zero /Rotate → back to the original orientation (WORM: no separate copy)
    }

    [Fact]
    public void Malformed_pdf_throws_before_any_change_fail_safe()
    {
        var garbage = System.Text.Encoding.UTF8.GetBytes("%PDF-1.7 this is not really a pdf");
        Assert.ThrowsAny<Exception>(() => PdfRotation.WithRotations(garbage, new Dictionary<int, int> { [1] = 90 }));
        Assert.ThrowsAny<Exception>(() => PdfRotation.ReadRotations(garbage));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(180, 180)]
    [InlineData(270, 270)]
    [InlineData(360, 0)]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    public void Snaps_non_orthogonal_or_out_of_range_angles_to_a_legal_rotate(int input, int expected)
    {
        var rotated = PdfRotation.WithRotations(MakePdf(), new Dictionary<int, int> { [1] = input });
        Assert.Equal(expected, PdfRotation.ReadRotations(rotated)[1]);
    }

    [Fact]
    public void Out_of_range_pages_are_ignored_not_a_crash()
    {
        var rotated = PdfRotation.WithRotations(MakePdf(1), new Dictionary<int, int> { [1] = 90, [5] = 180 });
        var r = PdfRotation.ReadRotations(rotated);
        Assert.Equal(90, r[1]);
        Assert.Single(r); // 1 page; page 5 silently ignored
    }
}
