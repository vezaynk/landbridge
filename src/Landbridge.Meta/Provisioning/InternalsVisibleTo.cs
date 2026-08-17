using System.Runtime.CompilerServices;

// InstanceReconciler's sweep is internal: in production the only thing that runs it is
// the reconciler's own timer, and exposing "reconcile now" as public API would invite a
// caller that skips the tracker mark the loop takes. The tests that prove convergence
// have to drive one deterministic pass rather than wait on a 30s interval, and they
// assert against the retry backoff by name, so they need internal visibility. No
// InternalsVisibleTo existed for Docket.Meta before this change; the file lives under
// Provisioning/ because that is the change's fence.
[assembly: InternalsVisibleTo("Docket.Meta.Tests")]
