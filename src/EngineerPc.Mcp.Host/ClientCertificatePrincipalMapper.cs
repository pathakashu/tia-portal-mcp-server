using System.Security.Cryptography.X509Certificates;
using System.Diagnostics.CodeAnalysis;
using EngineerPc.Contracts;

namespace EngineerPc.Mcp.Host;

public sealed record ClientCertificatePrincipalMapping
{
    public string CertificateThumbprint { get; init; } = string.Empty;

    public string[] Roles { get; init; } = [];

    public string[] Scopes { get; init; } = [];
}

public sealed class ClientCertificatePrincipalMapper
{
    private readonly IReadOnlyDictionary<string, ClientCertificatePrincipalMapping> mappings;

    public ClientCertificatePrincipalMapper(IEnumerable<ClientCertificatePrincipalMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);

        this.mappings = mappings.ToDictionary(
            mapping => NormalizeThumbprint(mapping.CertificateThumbprint),
            StringComparer.Ordinal);
    }

    public bool TryMap(
        X509Certificate2? certificate,
        [NotNullWhen(true)] out AuthenticatedPrincipal? principal)
    {
        principal = null;
        if (certificate is null || string.IsNullOrWhiteSpace(certificate.Thumbprint))
        {
            return false;
        }

        if (!mappings.TryGetValue(NormalizeThumbprint(certificate.Thumbprint), out var mapping))
        {
            return false;
        }

        principal = new AuthenticatedPrincipal(
            new AuthenticatedIdentity(certificate.Subject, certificate.Thumbprint),
            new HashSet<string>(mapping.Roles, StringComparer.Ordinal),
            new HashSet<string>(mapping.Scopes, StringComparer.Ordinal));
        return true;
    }

    public static bool IsValidThumbprint(string? thumbprint) =>
        !string.IsNullOrWhiteSpace(thumbprint) &&
        NormalizeThumbprint(thumbprint).Length == 40 &&
        NormalizeThumbprint(thumbprint).All(IsAsciiHexDigit);

    private static string NormalizeThumbprint(string thumbprint) =>
        string.Concat(thumbprint.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();

    private static bool IsAsciiHexDigit(char character) =>
        character is >= '0' and <= '9' or >= 'A' and <= 'F';
}