using System.Runtime.CompilerServices;

// §8.4 PEM hot-reload: PreviewCertificateProvider's current-cert accessor and its
// synchronous reload seam are internal. The Preview test assembly reads the current
// cert and forces a deterministic reload (bypassing the file watcher + debounce) so
// the swap / fail-safe / two-file-ordering cases can be asserted without racing the
// file system. No InternalsVisibleTo existed for Landbridge.Preview before this change.
[assembly: InternalsVisibleTo("Landbridge.Preview.Tests")]
