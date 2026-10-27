import { Component, effect, input, output, signal } from '@angular/core';
import { MatDatepickerInputEvent, MatDatepickerModule } from '@angular/material/datepicker';

/** German date field: free typing in TT.MM.JJJJ (strictly validated via GermanDateAdapter) plus a localized
 *  Material calendar popup (opens on field click). The wire value stays an ISO yyyy-MM-dd string ('' when
 *  empty), so the form model and server contract are untouched. `invalid` is read by the parent
 *  (viewChildren) to block submit — a typo flags the field instead of silently dropping the date. */
@Component({
  selector: 'app-date-input',
  imports: [MatDatepickerModule],
  templateUrl: './date-input.html',
  styleUrl: './date-input.css',
})
export class DateInput {
  value = input<string>('');           // ISO yyyy-MM-dd or ''
  valueChange = output<string>();

  invalid = signal(false);
  pickerValue = signal<Date | null>(null);
  private lastEmitted: string | null = null;

  constructor() {
    // Push the parent's value into the input ONLY when it isn't the echo of our own emit. Re-binding on
    // every keystroke would wipe the text mid-edit: deleting one digit makes the date momentarily
    // unparseable, we emit '', and a naive binding would clear the whole field under the user's cursor.
    effect(() => {
      const v = this.value() ?? '';
      if (v === this.lastEmitted) return;
      this.lastEmitted = v;
      const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(v);
      this.pickerValue.set(m ? new Date(+m[1], +m[2] - 1, +m[3]) : null);
      if (v) this.invalid.set(false); // a programmatic fill supersedes a flagged typo
    });
  }

  onInput(e: MatDatepickerInputEvent<Date>) {
    const text = (e.targetElement as HTMLInputElement).value?.trim() ?? '';
    let iso = '';
    if (e.value instanceof Date && !isNaN(e.value.getTime())) {
      const d = e.value; // local parts — toISOString() would shift across the UTC midnight boundary
      iso = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
      this.invalid.set(false);
    } else {
      this.invalid.set(text !== '');
    }
    this.lastEmitted = iso;
    this.valueChange.emit(iso);
  }
}
