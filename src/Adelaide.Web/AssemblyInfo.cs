using System.Runtime.CompilerServices;

// Testability seam (P6): expose the internal filing/reconcile state machine -- UploadProcessor's
// ConsumeAsync / ProcessAsync / ReconcileAsync (made internal) -- to the test project only. No runtime effect.
[assembly: InternalsVisibleTo("Adelaide.Web.Tests")]
