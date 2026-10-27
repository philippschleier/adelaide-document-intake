namespace Adelaide.Core;

/// <summary>
/// Patient identity derived from a decoded barcode payload — trusted ONLY when the payload is a structurally-valid
/// KBV document: a Muster 12 BFB (<see cref="BfbBarcode"/>) or a Bundeseinheitlicher Medikationsplan
/// (<see cref="BmpBarcode"/>). A decoded barcode is NOT, by itself, evidence of patient identity. With a stronger
/// decoder the pipeline now reads franking / routing DataMatrix codes (e.g. on Krankenkasse letters) that never
/// decoded before; a coincidentally checksum-valid KVNR-shaped token inside one of those must never become the
/// matcher's primary key (that would file a document under the wrong patient). Gating on the structural parse —
/// rather than scanning any payload for a KVNR token — closes that path.
/// </summary>
public static class BarcodeIdentity
{
    /// <summary>The patient KVNR carried by a Muster 12 BFB or a BMP payload, checksum-validated and upper-cased;
    /// null for any payload that is not one of those structural KBV forms (franking, routing, or arbitrary codes).</summary>
    public static string? StructuralKvnr(string? payload)
    {
        var kvnr = (BfbBarcode.Parse(payload)?.Kvnr ?? BmpBarcode.Parse(payload)?.Kvnr)?.Trim().ToUpperInvariant();
        return kvnr is not null && Kvnr.IsValid(kvnr) ? kvnr : null;
    }
}
