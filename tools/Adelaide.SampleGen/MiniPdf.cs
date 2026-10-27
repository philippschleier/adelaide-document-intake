using System.Globalization;
using System.Text;

namespace Adelaide.SampleGen;

/// <summary>A deliberately tiny, dependency-free PDF writer for the synthetic demo corpus: A4 pages, the two standard
/// Helvetica fonts (no embedding, WinAnsi encoding for German umlauts) and straight lines/rectangles. Output is
/// byte-deterministic — no /CreationDate, no /ID — so a fixture can be keyed by the file's SHA-256.</summary>
internal sealed class MiniPdf
{
    public const double Width = 595, Height = 842; // A4 in points

    private readonly List<StringBuilder> _pages = new();
    private StringBuilder Current => _pages[^1];

    public MiniPdf NewPage() { _pages.Add(new StringBuilder()); return this; }

    /// <summary>Text at (x, y) measured from the TOP-left, in points.</summary>
    public MiniPdf Text(double x, double y, string text, double size = 10, bool bold = false)
    {
        Current.Append(CultureInfo.InvariantCulture, $"BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {x:0.##} {Height - y:0.##} Td (")
               .Append(Escape(text)).Append(") Tj ET\n");
        return this;
    }

    public MiniPdf Line(double x1, double y1, double x2, double y2, double width = 0.6)
    {
        Current.Append(CultureInfo.InvariantCulture, $"{width:0.##} w {x1:0.##} {Height - y1:0.##} m {x2:0.##} {Height - y2:0.##} l S\n");
        return this;
    }

    public MiniPdf Box(double x, double y, double w, double h, double gray = -1, double width = 0.6)
    {
        var op = gray >= 0 ? $"{gray:0.##} g " : "";
        Current.Append(CultureInfo.InvariantCulture, $"{op}{width:0.##} w {x:0.##} {Height - y - h:0.##} {w:0.##} {h:0.##} re {(gray >= 0 ? "f" : "S")} 0 g\n");
        return this;
    }

    /// <summary>A checkbox with an optional cross, like a printed form field.</summary>
    public MiniPdf Check(double x, double y, bool ticked, string label)
    {
        Box(x, y - 8, 9, 9);
        if (ticked) { Line(x + 1.5, y - 6.5, x + 7.5, y - 0.5, 1); Line(x + 1.5, y - 0.5, x + 7.5, y - 6.5, 1); }
        return Text(x + 14, y, label, 10);
    }

    /// <summary>The banner every generated page carries, so no sample can be mistaken for a real record.</summary>
    public MiniPdf SyntheticBanner()
    {
        Box(40, 18, Width - 80, 18, gray: 0.88);
        return Text(48, 31, "SYNTHETISCHES MUSTERDOKUMENT - fiktive Daten, keine echte Person, Einrichtung oder Kasse", 8.5, bold: true);
    }

    public byte[] ToBytes()
    {
        // Objects: 1 catalog, 2 pages, 3 F1, 4 F2, then (page, content) pairs.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // pages tree, filled in once the kids are known
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
        };
        var kids = new List<int>();
        foreach (var page in _pages)
        {
            var content = page.ToString();
            var pageObj = objects.Count + 1;
            kids.Add(pageObj);
            objects.Add(string.Create(CultureInfo.InvariantCulture,
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {Width} {Height}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {pageObj + 1} 0 R >>"));
            objects.Add($"<< /Length {Latin1.GetByteCount(content)} >>\nstream\n{content}endstream");
        }
        objects[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids.Select(k => $"{k} 0 R"))}] /Count {kids.Count} >>";

        using var ms = new MemoryStream();
        void W(string s) { var b = Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = ms.Position;
        W($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static readonly Encoding Latin1 = Encoding.Latin1;

    /// <summary>PDF literal-string escaping + the handful of WinAnsi code points outside Latin-1 that German text uses.</summary>
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            var b = c switch
            {
                '–' => 0x96, '—' => 0x97, '„' => 0x84, '“' => 0x93, '”' => 0x94, '‚' => 0x82, '‘' => 0x91, '’' => 0x92,
                '€' => 0x80, '•' => 0x95, '…' => 0x85,
                _ => c <= 0xFF ? c : '?',
            };
            if (b is '(' or ')' or '\\') sb.Append('\\').Append((char)b);
            else if (b < 0x20 || b > 0x7E) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else sb.Append((char)b);
        }
        return sb.ToString();
    }
}
