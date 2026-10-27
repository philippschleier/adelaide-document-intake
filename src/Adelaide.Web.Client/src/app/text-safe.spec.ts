import { cleanName } from './text-safe';

describe('cleanName', () => {
  it('returns an empty string for null/undefined/empty', () => {
    expect(cleanName(null)).toBe('');
    expect(cleanName(undefined)).toBe('');
    expect(cleanName('')).toBe('');
  });

  it('leaves an ordinary German filename untouched', () => {
    expect(cleanName('Verordnung_Müller_2026-10-01.pdf')).toBe('Verordnung_Müller_2026-10-01.pdf');
  });

  it('strips a right-to-left override that would visually reorder the name', () => {
    const rlo = String.fromCodePoint(0x202e);
    expect(cleanName(`invoice${rlo}fdp.exe`)).toBe('invoicefdp.exe');
  });

  it('strips bidi isolates and C0/C1 control characters', () => {
    const s = `a${String.fromCodePoint(0x2066)}b${String.fromCodePoint(0x0007)}c${String.fromCodePoint(0x0085)}d`;
    expect(cleanName(s)).toBe('abcd');
  });
});
