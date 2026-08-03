using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Docket.Meta.Secrets;

/// <summary>
/// Applies <see cref="MetaSecretProtector"/> to one column, so the ciphertext is the
/// only form that ever reaches Postgres. Deliberately a value converter rather than
/// encrypt/decrypt calls at the use sites: the dangerous mistake is a future write
/// path that forgets to encrypt, which would silently persist plaintext, and a
/// converter makes that unrepresentable. EF applies converters only to non-null
/// values, so nullable columns need no special case here.
///
/// <para><paramref name="purpose"/> is bound into the ciphertext as associated data,
/// so a value cannot be replayed into a different column.</para>
/// </summary>
public sealed class EncryptedStringConverter(MetaSecretProtector protector, string purpose)
    : ValueConverter<string, string>(
        plain => protector.Protect(plain, purpose),
        stored => protector.Unprotect(stored, purpose));

/// <summary>
/// <see cref="EncryptedStringConverter"/> for a nullable column — a host's mTLS
/// client key, absent for the local unix socket. Null means "no key on this host" and
/// stays null; only a present key is sealed.
/// </summary>
public sealed class EncryptedNullableStringConverter(MetaSecretProtector protector, string purpose)
    : ValueConverter<string?, string?>(
        plain => plain == null ? null : protector.Protect(plain, purpose),
        stored => stored == null ? null : protector.Unprotect(stored, purpose));

/// <summary>
/// Keys EF's model cache on the protector's key SET as well as the context type.
/// Without this, two <see cref="Data.MetaDbContext"/> instances built with different
/// keys in one process would share the first one's compiled model — and therefore its
/// converters, and therefore its keys — silently sealing under the wrong key or failing
/// to read a row only the second one's retired key can open. Production has a single
/// singleton protector and never notices; a rotation and the test suite do.
/// </summary>
public sealed class MetaModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), (context as Data.MetaDbContext)?.KeySetId, designTime);
}
