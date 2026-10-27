import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { CompareData, DocRow, PatientCandidate, ReviewData, ReviewSubmission, Stats } from './models';

@Injectable({ providedIn: 'root' })
export class Api {
  private http = inject(HttpClient);

  documents(scope: string = 'active'): Observable<DocRow[]> {
    // scope = 'active' | 'all' | an exact state name (e.g. 'Filed') for the clickable count-chip filters
    return this.http.get<DocRow[]>('/api/documents', { params: { scope } });
  }

  stats(): Observable<Stats> {
    return this.http.get<Stats>('/api/stats');
  }

  upload(files: File[]): Observable<{ jobs: unknown[] }> {
    const fd = new FormData();
    for (const f of files) fd.append('files', f);
    return this.http.post<{ jobs: unknown[] }>('/api/upload', fd);
  }

  // Read-only page image for the shared page viewer — small WebP tiles for the rail, larger ones for the reading column
  // (w). `rot` (0/90/180/270) is ALWAYS in the URL: the caller passes the doc's savedRotation (0 when unrotated), sourced
  // from a payload it already loaded, so the URL fully determines the bytes. That determinism is what keeps every cache
  // layer correct — a re-orientation changes the URL, and an unchanged angle reuses the cache (incl. the browser's
  // in-memory image cache). Resolves the intake copy OR the filed PDF, so every surface works off the same URL.
  pageThumbUrl(id: string, page: number, w = 220, rot = 0): string {
    return `/api/page?id=${encodeURIComponent(id)}&page=${page}&w=${w}&rot=${rot}`;
  }

  // The real PDF page count for the viewer (how many page images to render). GET; same inbox-or-filed source.
  pageCountUrl(id: string): string { return `/api/pagecount?id=${encodeURIComponent(id)}`; }

  // The ORIGINAL, untouched PDF (no rotation, no split) — for the review top bar's Download. Same-origin, so an anchor
  // with `download` saves it. Deliberately omits the `rotate` param that the retired native viewer once used.
  fileUrl(id: string): string { return `/api/file?id=${encodeURIComponent(id)}`; }

  patients(q: string): Observable<PatientCandidate[]> {
    return this.http.get<PatientCandidate[]>('/api/patients', { params: { q } });
  }

  review(id: string): Observable<ReviewData> {
    return this.http.get<ReviewData>('/api/review', { params: { id } });
  }

  submitReview(body: ReviewSubmission): Observable<{ ok: boolean; fileName: string }> {
    return this.http.post<{ ok: boolean; fileName: string }>('/api/review', body);
  }

  // Persist the reviewer's chosen document orientation (absolute 0/90/180/270) as a document property — saved per rotate
  // click (no debounce), so it survives leaving/re-opening the queue. PUT (the global csrfInterceptor stamps the CSRF
  // header on mutating verbs); the submit handler AWAITS this response before POSTing, so the server-side bake can never
  // read a stale angle. Does NOT touch the stored PDF — the hash-chained /Rotate transform happens only at filing.
  saveRotation(id: string, angle: number): Observable<{ angle: number }> {
    return this.http.put<{ angle: number }>(`/api/documents/${encodeURIComponent(id)}/rotation`, { angle });
  }

  // Manual split (in review): the reviewer partitions the doc into ≥2 contiguous 1-based [first,last] page ranges that
  // cover 1..pageCount with no gaps/overlaps. Each range becomes a child that re-enters the pipeline; the parent leaves
  // the review queue (terminal Segmented). SEPARATE from filing — it does not file. Same CSRF-header POST pattern as
  // submitReview (the global csrfInterceptor stamps X-Requested-With on every mutating verb).
  split(body: { documentId: string; ranges: number[][]; names?: string[] }): Observable<{ ok: boolean; children: string[] }> {
    return this.http.post<{ ok: boolean; children: string[] }>('/api/split', body);
  }

  retry(id: string): Observable<{ ok: boolean }> {
    return this.http.post<{ ok: boolean }>('/api/retry', null, { params: { id } });
  }

  reprocess(id: string): Observable<{ ok: boolean; sharePointItemRemoved: boolean }> {
    return this.http.post<{ ok: boolean; sharePointItemRemoved: boolean }>('/api/reprocess', null, { params: { id } });
  }

  compare(id: string, other: string): Observable<CompareData> {
    return this.http.get<CompareData>('/api/compare', { params: { id, other } });
  }

  diffUrl(a: string, b: string, page: number): string {
    return `/api/diff?a=${encodeURIComponent(a)}&b=${encodeURIComponent(b)}&page=${page}`;
  }

  duplicateDiscard(id: string): Observable<{ ok: boolean }> {
    return this.http.post<{ ok: boolean }>('/api/duplicate-discard', null, { params: { id } });
  }

  duplicateProcess(id: string): Observable<{ ok: boolean }> {
    return this.http.post<{ ok: boolean }>('/api/duplicate-process', null, { params: { id } });
  }

  // Restore a discarded document to its pre-discard state (discard-recovery). The free-text reason is
  // required — it lands in the erasable projection, linked to the undiscard event.
  undiscard(id: string, reason: string): Observable<{ ok: boolean; restoredTo: string }> {
    return this.http.post<{ ok: boolean; restoredTo: string }>('/api/undiscard', { reason }, { params: { id } });
  }
}
