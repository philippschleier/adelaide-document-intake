namespace Adelaide.Core;

/// <summary>
/// Genehmigungsnummer (approval number) OCR repair. AOK Rheinland/Hamburg approval numbers are a leading capital
/// 'G' followed by digits; gpt-4o vision frequently misreads that 'G' as the look-alike digit '6' — the same glyph
/// confusion <see cref="Kvnr.Repair"/> handles for the KVNR's leading letter (it already lists 6↔G as a look-alike pair).
/// Unlike the KVNR there is no check digit to validate against, so the repair is deliberately conservative: it only
/// touches a value that is a leading '6' followed by digits ONLY (the G-format shape), and only the first character.
/// Anything else is returned unchanged — a number is never otherwise reshaped.
/// </summary>
public static class Approval
{
    public static string Repair(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var s = value.Trim();
        if (s.Length < 5 || s[0] != '6') return s;                  // not the leading-6 misread shape
        for (int i = 1; i < s.Length; i++)
            if (s[i] < '0' || s[i] > '9') return s;                 // must be '6' + digits only (no separators/letters)
        return 'G' + s[1..];
    }
}
