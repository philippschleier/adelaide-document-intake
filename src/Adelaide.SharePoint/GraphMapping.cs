using System.Globalization;
using System.Text.Json;
using Adelaide.Core;

namespace Adelaide.SharePoint;

/// <summary>Pure JSON→domain mapping and OData helpers for the Graph patient directory (testable without HTTP).</summary>
public static class GraphMapping
{
    /// <summary>Map a Graph list item (with "id" and an expanded "fields" object) to a <see cref="PatientRef"/>.</summary>
    public static PatientRef MapPatient(JsonElement item)
    {
        var f = item.TryGetProperty("fields", out var ff) ? ff : default;
        return new PatientRef(
            ListItemId: item.TryGetProperty("id", out var idEl) && int.TryParse(idEl.GetString(), out var id) ? id : 0,
            Kvnr: Text(f, "InsuranceNumber"),
            LastName: Text(f, "PatientLastName"),
            FirstName: Text(f, "PatientFirstName"),
            BirthDate: Date(f, "DateOfBirth"),
            Status: Text(f, "PatientStatus"),
            KlientId: Text(f, "KlientID"),
            PatientNr: Text(f, "PatientID"));
    }

    /// <summary>Escape a string for an OData single-quoted literal (doubles apostrophes) — prevents filter breakage/injection.</summary>
    public static string Escape(string s) => s.Replace("'", "''");

    /// <summary>Read a string property, or "" if the element isn't an object / the property is missing or non-string.</summary>
    public static string Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : "";

    // SharePoint serialises a date-only value (local midnight) to UTC, so a Berlin DOB of 13 Apr is returned as
    // "…-12T22:00:00Z". Taking .UtcDateTime would read 12 Apr — a one-day shift that breaks name+DOB matching.
    // Round to the nearest midnight (+12h) to recover the intended calendar date for any ±12h offset (covers
    // Europe); genuine UTC-midnight date-only values are unaffected.
    private static DateOnly? Date(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
            ? DateOnly.FromDateTime(dt.UtcDateTime.AddHours(12))
            : null;
}
