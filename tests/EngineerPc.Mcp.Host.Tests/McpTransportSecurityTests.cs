using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using EngineerPc.Mcp.Host;
using EngineerPc.Tia.V18.Client;

namespace EngineerPc.Mcp.Host.Tests;

public sealed class McpTransportSecurityTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Validate_WithDefaultProductionOptions_FailsClosed()
    {
        var result = McpTransportOptionsValidator.Validate(new McpTransportOptions());

        Assert.False(result.IsValid);
        Assert.Contains("A server certificate path is required when insecure localhost mode is disabled.", result.Errors);
        Assert.Contains("At least one trusted client issuer is required when insecure localhost mode is disabled.", result.Errors);
        Assert.Contains("At least one client certificate principal mapping is required when insecure localhost mode is disabled.", result.Errors);
    }

    [Fact]
    public void Validate_WithExplicitInsecureLocalhostOptions_AllowsDevelopmentConfiguration()
    {
        var result = McpTransportOptionsValidator.Validate(new McpTransportOptions
        {
            AllowInsecureLocalhost = true
        });

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_WithInvalidSessionDuration_RejectsConfiguration()
    {
        var result = McpTransportOptionsValidator.Validate(new McpTransportOptions
        {
            AllowInsecureLocalhost = true,
            SessionDurationSeconds = 0
        });

        Assert.False(result.IsValid);
        Assert.Contains("MCP session duration must be between 1 and 3600 seconds.", result.Errors);
    }

    [Fact]
    public void TiaV18WorkerOptions_WhenEnabledWithMissingPaths_FailClosed()
    {
        var options = new TiaV18WorkerHostOptions
        {
            Enabled = true,
            WorkerExecutablePath = "C:\\Missing\\EngineerPc.Tia.V18.Worker.exe",
            ConfigurationPath = "C:\\Missing\\worker.json",
            RequestTimeoutSeconds = 30
        };

        Assert.Throws<ArgumentException>(() => new TiaV18WorkerClient(options.ToClientOptions()));
    }

    [Fact]
    public void TiaV18WorkerOptions_WhenDisabled_CannotProduceClientOptions()
    {
        Assert.Throws<InvalidOperationException>(() => new TiaV18WorkerHostOptions().ToClientOptions());
    }

    [Fact]
    public void Validate_WithTrustedCurrentClientCertificate_AllowsCertificate()
    {
        using var certificate = CreateCertificate("CN=Engineer-PC Test CA");
        var validator = new ClientCertificateValidator([certificate.Issuer], new TestTimeProvider(NowUtc));

        var isValid = validator.Validate(certificate, null, SslPolicyErrors.None);

        Assert.True(isValid);
    }

    [Fact]
    public void Validate_WithUntrustedIssuerOrChainError_RejectsCertificate()
    {
        using var certificate = CreateCertificate("CN=Engineer-PC Test CA");
        var validator = new ClientCertificateValidator(["CN=Other CA"], new TestTimeProvider(NowUtc));

        var untrustedIssuerIsValid = validator.Validate(certificate, null, SslPolicyErrors.None);
        var chainErrorIsValid = validator.Validate(certificate, null, SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.False(untrustedIssuerIsValid);
        Assert.False(chainErrorIsValid);
    }

    [Fact]
    public void TryMap_WithConfiguredClientThumbprint_ReturnsConfiguredPrincipal()
    {
        using var certificate = CreateCertificate("CN=Approved Engineer");
        var mapper = new ClientCertificatePrincipalMapper([
            new ClientCertificatePrincipalMapping
            {
                CertificateThumbprint = certificate.Thumbprint!,
                Roles = ["Engineer"],
                Scopes = ["engineering.plan"]
            }
        ]);

        var isMapped = mapper.TryMap(certificate, out var principal);

        Assert.True(isMapped);
        Assert.NotNull(principal);
        Assert.Equal(certificate.Subject, principal.Identity.SubjectId);
        Assert.Contains("Engineer", principal.Roles);
        Assert.Contains("engineering.plan", principal.Scopes);
    }

    [Fact]
    public void TryMap_WithUnmappedClientThumbprint_RejectsCertificate()
    {
        using var certificate = CreateCertificate("CN=Unapproved Engineer");
        var mapper = new ClientCertificatePrincipalMapper([
            new ClientCertificatePrincipalMapping
            {
                CertificateThumbprint = new string('A', 40),
                Roles = ["Engineer"],
                Scopes = ["engineering.plan"]
            }
        ]);

        var isMapped = mapper.TryMap(certificate, out var principal);

        Assert.False(isMapped);
        Assert.Null(principal);
    }

    private static X509Certificate2 CreateCertificate(string subjectName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            subjectName,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(NowUtc.AddDays(-1), NowUtc.AddDays(1));
    }

    private sealed class TestTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}