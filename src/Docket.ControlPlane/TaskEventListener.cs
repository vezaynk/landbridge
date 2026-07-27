using System.Threading.Channels;
using Npgsql;

namespace Docket.ControlPlane;

/// <summary>
/// Listens for task-event NOTIFYs on a dedicated connection (§3.1: LISTEN
/// needs a session-mode or direct connection, outside the pool). Yields the
/// task id carried by each notification so a caller — the dispatch loop, a
/// subscribed Lead view — can react without polling.
/// </summary>
public sealed class TaskEventListener(string connectionString) : IAsyncDisposable
{
    private NpgsqlConnection? _connection;

    /// <summary>
    /// Opens the listening connection and streams notified task ids until the
    /// token is cancelled. One consumer per listener.
    /// </summary>
    public async IAsyncEnumerable<Guid> ListenAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<Guid>();
        _connection = new NpgsqlConnection(connectionString);
        _connection.Notification += (_, e) =>
        {
            if (Guid.TryParse(e.Payload, out var id))
                channel.Writer.TryWrite(id);
        };

        await _connection.OpenAsync(ct);
        await using (var listen = new NpgsqlCommand($"LISTEN {DocketDbContext.EventChannel}", _connection))
            await listen.ExecuteNonQueryAsync(ct);

        // Pump WaitAsync in the background; it raises Notification as messages
        // land and blocks otherwise. The channel decouples that from iteration.
        var pump = PumpAsync(_connection, channel.Writer, ct);

        try
        {
            await foreach (var id in channel.Reader.ReadAllAsync(ct))
                yield return id;
        }
        finally
        {
            await pump;
        }
    }

    private static async Task PumpAsync(NpgsqlConnection connection, ChannelWriter<Guid> writer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
                await connection.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }
        finally
        {
            writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
