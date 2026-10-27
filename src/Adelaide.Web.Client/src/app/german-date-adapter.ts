import { Injectable } from '@angular/core';
import { NativeDateAdapter } from '@angular/material/core';

/** Date adapter for German document work: parses typed TT.MM.JJJJ strictly (also / and - separators,
 *  two-digit years with a 1950 pivot for birth dates) and always formats dd.MM.yyyy — independent of the
 *  browser's UI locale, which is what made the native date input render mm/dd/yyyy on en-US machines. */
@Injectable()
export class GermanDateAdapter extends NativeDateAdapter {
  override parse(value: unknown): Date | null {
    if (typeof value === 'string') {
      const t = value.trim();
      if (!t) return null;
      const m = /^(\d{1,2})[./-](\d{1,2})[./-](\d{2}|\d{4})$/.exec(t);
      if (!m) return this.invalid();
      const day = +m[1], month = +m[2];
      let year = +m[3];
      if (m[3].length === 2) year += year >= 50 ? 1900 : 2000;
      const d = new Date(year, month - 1, day);
      // reject impossible dates like 31.02. (Date would silently roll them over)
      return d.getFullYear() === year && d.getMonth() === month - 1 && d.getDate() === day ? d : this.invalid();
    }
    return super.parse(value);
  }

  override format(date: Date, displayFormat: object): string {
    // dd.MM.yyyy only for day-bearing formats (the input field). Month/year labels — the calendar
    // header above the grid — keep their locale formatting ("Mai 2026"); a blanket override here put
    // a full date into that header, which read like a selected day while merely browsing months.
    if (displayFormat && typeof displayFormat === 'object' && 'day' in displayFormat) {
      return `${String(date.getDate()).padStart(2, '0')}.${String(date.getMonth() + 1).padStart(2, '0')}.${date.getFullYear()}`;
    }
    return super.format(date, displayFormat);
  }

  override getFirstDayOfWeek(): number {
    return 1; // Monday — explicit, in case the runtime lacks Intl weekInfo
  }
}
