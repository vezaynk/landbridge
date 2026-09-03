using Landbridge.ControlPlane.Tests;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Mcp.Tests;

internal static class TestPublicIds
{
    public static async Task<SessionId> SessionAsync(PostgresFixture pg, string publicId, CancellationToken ct)
    {
        await using var db = pg.NewContext();
        var id = await db.Sessions.AsNoTracking()
            .Where(s => s.Slug == publicId)
            .Select(s => s.Id)
            .SingleAsync(ct);
        return new SessionId(id);
    }
}
