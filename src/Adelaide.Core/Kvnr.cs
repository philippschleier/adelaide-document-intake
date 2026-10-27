using System.Globalization;

namespace Adelaide.Core;

/// <summary>
/// German Krankenversichertennummer (KVNR): 1 letter + 9 digits, last is a mod-10 check digit
/// (letter → 2-digit code A=01..Z=26, then weights 1,2,1,2… with cross-sum of products).
/// Validating before lookup turns OCR misreads into "no match → review" instead of wrong-patient.
/// </summary>
public static class Kvnr
{
    public static bool IsValid(string? kvnr)
    {
        if (string.IsNullOrWhiteSpace(kvnr)) return false;
        kvnr = kvnr.Trim().ToUpperInvariant();
        if (kvnr.Length != 10) return false;
        if (kvnr[0] < 'A' || kvnr[0] > 'Z') return false;
        for (int i = 1; i < 10; i++)
            if (kvnr[i] < '0' || kvnr[i] > '9') return false;

        int letter = kvnr[0] - 'A' + 1;                                  // A=1 … Z=26
        string digits = letter.ToString("D2", CultureInfo.InvariantCulture) + kvnr.Substring(1, 8);    // 2 + 8 = 10 digits
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int p = (digits[i] - '0') * ((i % 2 == 0) ? 1 : 2);
            sum += p > 9 ? p - 9 : p;
        }
        return sum % 10 == kvnr[9] - '0';
    }

    /// <summary>OCR frequently reads the LEADING LETTER of a KVNR as a look-alike digit (O→0, I→1, S→5, B→8, …) — a
    /// gesetzliche-Kasse letter (e.g. AOK) always starts with a letter, so an all-digit value is a misread. Try the
    /// letter look-alikes for the first character and return the one — and only one — that yields a checksum-valid
    /// KVNR. Null if it's not this error or the correction is ambiguous: a patient identifier is never guessed.</summary>
    public static string? Repair(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToUpperInvariant();
        if (s.Length != 10 || s[0] < '0' || s[0] > '9') return null;       // not "leading letter read as a digit"
        for (int i = 1; i < 10; i++) if (s[i] < '0' || s[i] > '9') return null; // the other nine must already be digits

        string? repaired = null;
        foreach (var letter in LookAlikes(s[0]))
        {
            var candidate = letter + s[1..];
            if (!IsValid(candidate)) continue;
            if (repaired is not null) return null;                          // two letters validate → ambiguous, don't guess
            repaired = candidate;
        }
        return repaired;
    }

    private static char[] LookAlikes(char digit) => digit switch
    {
        '0' => new[] { 'O', 'D', 'Q' },
        '1' => new[] { 'I', 'L' },
        '2' => new[] { 'Z' },
        '5' => new[] { 'S' },
        '6' => new[] { 'G' },
        '8' => new[] { 'B' },
        _ => System.Array.Empty<char>(),
    };
}
