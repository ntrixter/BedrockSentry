using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;

namespace BedrockSentry.Services.Security;

/// <summary>
/// The names <c>Sentry:Tls:AdditionalSubjectAltNames</c> asks the generated certificate to
/// carry, and which of them a stored certificate lacks (<c>T324</c>).
/// </summary>
/// <remarks>
/// The generator writes the names only when it makes a certificate, so a name added to the
/// configuration after the first start was never applied, and nothing said so. The stored
/// certificate is now checked against the configuration at every start. One reading of an
/// entry serves both the generator and the check, so the two cannot disagree about what an
/// entry means.
/// </remarks>
public static class CertificateNames
{
    private const string SubjectAltNameOid = "2.5.29.17";

    /// <summary>The configured entries, as addresses and DNS names.</summary>
    /// <remarks>
    /// An entry that parses as an IP address is an address, written into the certificate as
    /// one; anything else is a DNS name, trimmed. Blank entries are skipped.
    /// </remarks>
    public static (IReadOnlyList<IPAddress> Addresses, IReadOnlyList<string> DnsNames) Split(
        IEnumerable<string> configured)
    {
        ArgumentNullException.ThrowIfNull(configured);

        var addresses = new List<IPAddress>();
        var names = new List<string>();

        foreach (var entry in configured)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (IPAddress.TryParse(entry, out var address))
            {
                addresses.Add(address);
            }
            else
            {
                names.Add(entry.Trim());
            }
        }

        return (addresses, names);
    }

    /// <summary>The configured entries <paramref name="certificate"/> does not carry.</summary>
    /// <remarks>
    /// A DNS name is compared in the ASCII form a certificate stores, ignoring case. An entry
    /// the IDN mapping refuses, such as a label over 63 characters, is not reported: the
    /// generator refuses it too, so a replacement could not carry it.
    /// </remarks>
    public static IReadOnlyList<string> Missing(X509Certificate2 certificate, IEnumerable<string> configured)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        var (addresses, names) = Split(configured);

        var carried = certificate.Extensions
            .Where(e => e.Oid?.Value == SubjectAltNameOid)
            .Select(e => new X509SubjectAlternativeNameExtension(e.RawData))
            .ToList();

        var carriedAddresses = carried.SelectMany(e => e.EnumerateIPAddresses()).ToHashSet();
        var carriedNames = carried
            .SelectMany(e => e.EnumerateDnsNames())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missing = addresses
            .Where(a => !carriedAddresses.Contains(a))
            .Select(a => a.ToString())
            .ToList();

        var idn = new IdnMapping();

        foreach (var name in names)
        {
            string ascii;

            try
            {
                ascii = idn.GetAscii(name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (!carriedNames.Contains(ascii))
            {
                missing.Add(name);
            }
        }

        return missing;
    }
}
