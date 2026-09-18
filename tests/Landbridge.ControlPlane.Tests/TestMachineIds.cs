using System.Security.Cryptography;
using System.Text;

namespace Landbridge.ControlPlane.Tests;

/// <summary>
/// A stable machine id for a readable name. Machine identity is a
/// <see cref="Guid"/>, but a test reads better saying "m1" than a literal uuid,
/// and several assert that two references name the same box — so the mapping has
/// to be a function of the name, not a fresh id per call.
/// </summary>
internal static class TestMachineIds
{
    public static Guid For(string name) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes(name)));
}
