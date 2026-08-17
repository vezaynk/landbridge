using System.Collections.Concurrent;

namespace Docket.Runner;

/// <summary>
/// One dedicated, long-lived OS thread that every worker
/// <see cref="System.Diagnostics.Process.Start()"/> is marshalled onto (PDEATHSIG thread affinity).
///
/// <para><b>Why.</b> The Linux harness arms <c>prctl(PR_SET_PDEATHSIG, SIGKILL)</c>
/// so it dies the instant docketd does — but the kernel keys that death signal to
/// the <em>thread</em> that forked the child, not to the parent process. .NET
/// thread-pool threads are transient: the pool retires idle ones. If docketd forked
/// a worker from a pool thread that later retired, the kernel would deliver the
/// SIGKILL to a perfectly healthy worker. Pinning every fork to a single thread that
/// lives for docketd's whole lifetime removes the footgun. Spawns are rare, so one
/// thread parked between spawns costs nothing.</para>
///
/// <para><b>Shape.</b> The thread starts lazily on the first spawn and is a
/// background thread, so it never keeps docketd alive and is reclaimed at process
/// exit (or explicitly via <see cref="Close"/>). Work is serialized through a
/// <see cref="BlockingCollection{T}"/>; <see cref="Run"/> blocks the caller until the
/// delegate has completed on the spawner thread and re-throws any exception in the
/// caller's context — so a caller sees behaviour identical to an inline
/// <c>Process.Start()</c>.</para>
/// </summary>
internal sealed class SpawnerThread
{
    private readonly BlockingCollection<WorkItem> _queue = new();
    private readonly object _startGate = new();
    private Thread? _thread;
    private int _managedThreadId;

    /// <summary>
    /// Managed id of the dedicated spawner thread, or null before the first spawn.
    /// A test seam (PDEATHSIG thread affinity) — lets a test confirm every spawn ran on the one thread.
    /// </summary>
    internal int? ManagedThreadId
    {
        get
        {
            var id = Volatile.Read(ref _managedThreadId);
            return id == 0 ? null : id;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the dedicated spawner thread, blocking until it
    /// completes and re-throwing any exception it raised in the caller's context.
    /// </summary>
    public void Run(Action work)
    {
        EnsureStarted();
        var item = new WorkItem(work);
        _queue.Add(item);
        item.Completion.Task.GetAwaiter().GetResult();
    }

    /// <summary>Stops accepting work and lets the spawner thread exit. Idempotent.</summary>
    public void Close() => _queue.CompleteAdding();

    private void EnsureStarted()
    {
        if (Volatile.Read(ref _thread) is not null)
            return;

        lock (_startGate)
        {
            if (_thread is not null)
                return;

            var thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "docketd-spawner",
            };
            thread.Start();
            _thread = thread;
        }
    }

    private void Loop()
    {
        Volatile.Write(ref _managedThreadId, Environment.CurrentManagedThreadId);
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                item.Work();
                item.Completion.SetResult();
            }
            catch (Exception e)
            {
                item.Completion.SetException(e);
            }
        }
    }

    private sealed class WorkItem(Action work)
    {
        public Action Work { get; } = work;

        // RunContinuationsAsynchronously so the blocked caller's continuation never
        // runs inline on the spawner thread and stalls the next spawn.
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
