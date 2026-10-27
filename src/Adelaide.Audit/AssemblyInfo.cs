using System.Runtime.CompilerServices;

// Expose the internal queue-predicate constants (AuditQueryPredicates) to the test project only, so the predicate
// parity/regression tests can assert on the exact WHERE clauses BOTH stores execute. No runtime effect.
[assembly: InternalsVisibleTo("Adelaide.Audit.Tests")]
