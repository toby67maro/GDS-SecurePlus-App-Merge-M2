using System.Security.Cryptography.X509Certificates;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace ArchiTech.Api.Integrations;

public interface IPlannerFlowTokenProvider
{
    Task<string> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed class PlannerFlowTokenProvider : IPlannerFlowTokenProvider, IDisposable
{
    private const string FlowScope = "https://service.flow.microsoft.com//.default";
    private readonly PlannerFlowOptions _options;
    private readonly object _lock = new();
    private ClientCertificateCredential? _credential;
    private X509Certificate2? _certificate;

    public PlannerFlowTokenProvider(IOptions<PlannerFlowOptions> options) => _options = options.Value;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        ClientCertificateCredential credential;
        lock (_lock)
        {
            if (_credential is null)
            {
                var certificate = LoadCertificate();
                try
                {
                    _credential = new ClientCertificateCredential(_options.TenantId, _options.ClientId, certificate);
                    _certificate = certificate;
                }
                catch
                {
                    certificate.Dispose();
                    throw;
                }
            }
            credential = _credential;
        }
        var token = await credential.GetTokenAsync(new TokenRequestContext([FlowScope]), cancellationToken);
        return token.Token;
    }

    private X509Certificate2 LoadCertificate()
    {
        if (!string.IsNullOrWhiteSpace(_options.CertificatePath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(
                _options.CertificatePath, _options.CertificatePassword);
        }

        var thumbprint = _options.CertificateThumbprint
            ?? throw new System.Security.Cryptography.CryptographicException("Certificate thumbprint is required.");
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: true);
        if (matches.Count != 1 || !matches[0].HasPrivateKey)
        {
            foreach (var match in matches) match.Dispose();
            throw new System.Security.Cryptography.CryptographicException(
                "A single valid certificate with a private key is required.");
        }
        return matches[0];
    }

    public void Dispose() => _certificate?.Dispose();
}
