// FINDING 7: strip control characters and Unicode bidirectional overrides/isolates (U+202A-202E, U+2066-2069) from an
// untrusted document name before it is shown to a user. A filename is extracted from an uploaded document and can carry
// a right-to-left override that visually reorders the surrounding text — e.g. to misrepresent which document a discard
// or supersede confirmation applies to. Angular escapes HTML in interpolation, but window.confirm() renders raw text and
// bidi reordering survives escaping, so we neutralise it at the source here. Code points are checked numerically so this
// file stays pure ASCII (no invisible characters embedded in source).
export function cleanName(s: string | null | undefined): string {
  if (!s) return '';
  let out = '';
  for (const ch of s) {
    const c = ch.codePointAt(0) ?? 0;
    const unsafe =
      c <= 0x1f ||                    // C0 control characters
      (c >= 0x7f && c <= 0x9f) ||     // DEL + C1 control characters
      (c >= 0x202a && c <= 0x202e) || // LRE RLE PDF LRO RLO
      (c >= 0x2066 && c <= 0x2069);   // LRI RLI FSI PDI
    if (!unsafe) out += ch;
  }
  return out;
}
