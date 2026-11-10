using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace BedrockSentry.Core.Broker;

/// <summary>A service's SID, <c>NT SERVICE\&lt;name&gt;</c>, worked out from its name.</summary>
/// <remarks>
/// Windows derives it the same way whether or not the service is installed:
/// <c>S-1-5-80-</c> and the SHA-1 of the upper-case name in UTF-16LE, as five
/// little-endian sub-authorities. So the installer can grant a data root before it
/// registers the service, while nothing it would replace has been touched.
/// </remarks>
public static class ServiceSid
{
    public static string For(string serviceName)
    {
        var hash = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        var parts = Enumerable.Range(0, 5).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(hash.AsSpan(i * 4)));

        return "S-1-5-80-" + string.Join('-', parts);
    }
}
