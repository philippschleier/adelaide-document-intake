using System.Text.Json;
using Adelaide.Core;
using Adelaide.SharePoint;
using Xunit;

namespace Adelaide.SharePoint.Tests;

public class GraphMappingTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void MapPatient_maps_all_fields()
    {
        var item = Parse("{\"id\":\"42\",\"fields\":{\"InsuranceNumber\":\"Z000000005\",\"PatientLastName\":\"Mustermann\",\"PatientFirstName\":\"Max\",\"DateOfBirth\":\"1956-05-29T00:00:00Z\",\"PatientStatus\":\"Active\",\"KlientID\":\"uuid-1\",\"PatientID\":\"4711\"}}");
        var p = GraphMapping.MapPatient(item);
        Assert.Equal(42, p.ListItemId);
        Assert.Equal("Z000000005", p.Kvnr);
        Assert.Equal("Mustermann", p.LastName);
        Assert.Equal("Max", p.FirstName);
        Assert.Equal(new DateOnly(1956, 5, 29), p.BirthDate);
        Assert.Equal("Active", p.Status);
        Assert.Equal("uuid-1", p.KlientId);
        Assert.Equal("4711", p.PatientNr);
    }

    [Fact]
    public void MapPatient_handles_missing_optional_fields()
    {
        var p = GraphMapping.MapPatient(Parse("{\"id\":\"7\",\"fields\":{\"PatientLastName\":\"Beispiel\"}}"));
        Assert.Equal(7, p.ListItemId);
        Assert.Equal("Beispiel", p.LastName);
        Assert.Equal("", p.Kvnr);
        Assert.Null(p.BirthDate);
        Assert.Equal("", p.KlientId);
    }

    [Theory]
    [InlineData("{\"id\":\"1\",\"fields\":{\"DateOfBirth\":\"not-a-date\"}}")]
    [InlineData("{\"id\":\"1\",\"fields\":{\"DateOfBirth\":null}}")]
    [InlineData("{\"id\":\"1\"}")] // no fields object at all
    public void Bad_or_missing_birthdate_is_null(string json) =>
        Assert.Null(GraphMapping.MapPatient(Parse(json)).BirthDate);

    [Fact]
    public void MapPatient_non_integer_id_falls_back_to_zero() =>
        Assert.Equal(0, GraphMapping.MapPatient(Parse("{\"id\":\"abc\",\"fields\":{}}")).ListItemId);

    [Theory]
    [InlineData("O'Brien", "O''Brien")]      // apostrophe doubled (OData injection safety)
    [InlineData("Müller", "Müller")]          // umlaut untouched
    [InlineData("d'Arc'", "d''Arc''")]        // multiple apostrophes
    public void Escape_doubles_apostrophes(string input, string expected) =>
        Assert.Equal(expected, GraphMapping.Escape(input));
}
