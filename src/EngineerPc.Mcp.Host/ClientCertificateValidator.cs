using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace EngineerPc.Mcp.Host;

public sealed class ClientCertificateValidator
{
    private readonly HashSet<string> trustedIssuers;
    private readonly TimeProvider timeProvider;

    public ClientCertificateValidator(IEnumerable<string> trustedIssuers, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(trustedIssuers);

        this.trustedIssuers = trustedIssuers.ToHashSet(StringComparer.Ordinal);
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool Validate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors policyErrors)
    {
        if (certificate is null || policyErrors != SslPolicyErrors.None)
        {
            return false;
        }

        var nowUtc = timeProvider.GetUtcNow();
        if (certificate.NotBefore.ToUniversalTime() > nowUtc || certificate.NotAfter.ToUniversalTime() <= nowUtc)
        {
            return false;
        }

        return trustedIssuers.Contains(certificate.Issuer);
    }
}