import { HttpInterceptorFn } from '@angular/common/http';

// CSRF (FINDING 2): the API rejects any state-changing request that does not carry a custom header (403). A cross-site
// <form> cannot set request headers at all, and a cross-site fetch() that adds one triggers a CORS preflight the API
// never answers — so the header's presence proves the call came from our own same-origin SPA. We set it on the mutating
// verbs only; GETs (incl. the PDF fetch) are untouched. setHeaders adds ONLY this header and never Content-Type, so a
// FormData upload keeps the multipart boundary the browser generated.
const MUTATING = new Set(['POST', 'PUT', 'PATCH', 'DELETE']);

export const csrfInterceptor: HttpInterceptorFn = (req, next) => {
  if (MUTATING.has(req.method.toUpperCase())) {
    req = req.clone({ setHeaders: { 'X-Requested-With': 'XMLHttpRequest' } });
  }
  return next(req);
};
