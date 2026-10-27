import { TestBed } from '@angular/core/testing';
import { MAT_DATE_LOCALE } from '@angular/material/core';
import { GermanDateAdapter } from './german-date-adapter';

describe('GermanDateAdapter', () => {
  let adapter: GermanDateAdapter;
  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [GermanDateAdapter, { provide: MAT_DATE_LOCALE, useValue: 'de-DE' }] });
    adapter = TestBed.inject(GermanDateAdapter);
  });
  const ymd = (d: Date | null) => (d && !isNaN(d.getTime()) ? [d.getFullYear(), d.getMonth() + 1, d.getDate()] : null);

  it('parses TT.MM.JJJJ strictly, with . / - separators', () => {
    expect(ymd(adapter.parse('29.05.1956'))).toEqual([1956, 5, 29]);
    expect(ymd(adapter.parse('1/2/2026'))).toEqual([2026, 2, 1]);
    expect(ymd(adapter.parse('01-12-2026'))).toEqual([2026, 12, 1]);
  });

  it('pivots two-digit years at 50 (birth dates are 19xx, recent documents 20xx)', () => {
    expect(ymd(adapter.parse('17.03.39'))).toEqual([2039, 3, 17]);
    expect(ymd(adapter.parse('17.03.56'))).toEqual([1956, 3, 17]);
  });

  it('rejects impossible calendar dates instead of rolling them over', () => {
    expect(ymd(adapter.parse('31.02.2026'))).toBeNull();
    expect(ymd(adapter.parse('2026-02-01'))).toBeNull(); // ISO is not a typed German date
  });

  it('formats day-bearing formats as dd.MM.yyyy regardless of UI locale', () => {
    expect(adapter.format(new Date(2026, 0, 5), { year: 'numeric', month: 'numeric', day: 'numeric' })).toBe('05.01.2026');
  });

  it('starts the week on Monday', () => {
    expect(adapter.getFirstDayOfWeek()).toBe(1);
  });
});
