namespace Landbridge.Core;

/// <summary>
/// How a preview mapping admits a browser connection (spec §8.4). Chosen at
/// mint and stored on the mapping; may be flipped later. The preview frontend
/// delegates the check to the control plane on connect. Domain-neutral like
/// every other Core enum — the control plane never interprets what a preview is
/// <em>for</em>.
/// </summary>
public enum PreviewAuthPolicy
{
    /// <summary>
    /// The default. The request is admitted only when it carries a valid §12
    /// operator session (a Human, or a Lead on the mapping's Team) — the same
    /// credential the dashboard uses.
    /// </summary>
    Gated,

    /// <summary>
    /// A capability URL: the unguessable label alone admits the request. Same
    /// idle TTL as gated (2 hours by default, sliding on each admitted
    /// connection); public stays time-boxed by a requested-TTL ceiling.
    /// </summary>
    Public,
}
