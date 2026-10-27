using Adelaide.Ai;
using Xunit;

namespace Adelaide.Core.Tests;

/// <summary>The DI selection-marks fallback for Erst-/Folgeverordnung (used when a Verordnung has no readable BFB
/// barcode). The two boxes sit just left of the "Erstver…"/"Folgever…" labels; the sole ticked one wins, and
/// anything ambiguous returns null so the field falls back to vision rather than guessing.</summary>
public class LayoutPrescriptionOrderTests
{
    // A mark on the Erst/Folge row (y≈0.19) at horizontal position cx.
    private static SelectionMark Mark(double cx, bool selected, double conf = 0.97) => new(1, selected, conf, cx, 0.19);
    private static LayoutLine Label(string content, double cx) => new(1, content, cx, 0.19, cx - 0.03, 0.18, cx + 0.03, 0.20);

    // Erst box at 0.06, Folge box at 0.17; labels "Erstver-" at 0.11 and "Folgever-" at 0.22.
    private static LayoutResult Layout(bool erstChecked, bool folgeChecked, double conf = 0.97) => new(
        new[] { Mark(0.06, erstChecked, conf), Mark(0.17, folgeChecked, conf) },
        new[] { Label("Erstver-", 0.11), Label("Folgever-", 0.22) });

    [Fact] public void Folge_box_ticked_is_Folgeverordnung() => Assert.Equal("Folgeverordnung", Layout(false, true).PrescriptionOrder());
    [Fact] public void Erst_box_ticked_is_Erstverordnung() => Assert.Equal("Erstverordnung", Layout(true, false).PrescriptionOrder());

    [Fact]
    public void Both_or_neither_ticked_is_null()
    {
        Assert.Null(Layout(true, true).PrescriptionOrder());
        Assert.Null(Layout(false, false).PrescriptionOrder());
    }

    [Fact]
    public void Missing_label_is_null()
    {
        var noFolge = new LayoutResult(new[] { Mark(0.06, true, 0.97), Mark(0.17, false, 0.97) }, new[] { Label("Erstver-", 0.11) });
        Assert.Null(noFolge.PrescriptionOrder());
    }

    [Fact] public void Low_confidence_marks_are_ignored() => Assert.Null(Layout(false, true, conf: 0.10).PrescriptionOrder());
}
