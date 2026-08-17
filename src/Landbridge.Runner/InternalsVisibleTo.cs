using System.Runtime.CompilerServices;

// §10 Windows containment/PDEATHSIG thread affinity: the Windows Job Object containment primitive and the dedicated
// spawner-thread test seams are internal. The Runner test assembly needs to
// construct a Job Object directly (the kill-on-close containment proof) and read
// the per-task job + the captured spawn-thread identity. No InternalsVisibleTo
// existed before this change.
[assembly: InternalsVisibleTo("Landbridge.Runner.Tests")]
