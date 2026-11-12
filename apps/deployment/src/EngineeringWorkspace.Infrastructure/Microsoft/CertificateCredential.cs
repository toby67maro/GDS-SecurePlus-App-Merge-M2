using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace EngineeringWorkspace.Infrastructure.Microsoft;

/// <summary>
/// Certificate credentials for the tenant app registrations (4.0 beta.2). A self-signed RSA certificate is created on the
/// engineer's workstation; only its public key goes to Microsoft Entra. The PKCS#12 (private key included) is kept in the
/// encrypted secret store under the registration's secret reference, prefixed with <see cref="Prefix"/>, in the slot a
/// client secret used. It is never written to disk, logged, exported or passed on a command line.
/// </summary>
public static class CertificateCredential
{
    public const string Prefix = "pfx:";

    public sealed record Created(string StoredValue, byte[] PublicCertificate, string Thumbprint, DateTimeOffset NotAfterUtc);

    public static bool IsCertificate(string? stored) => stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Creates a 2048-bit RSA, SHA-256 self-signed certificate for client authentication.</summary>
    public static Created Create(string subjectName, TimeSpan lifetime, DateTimeOffset nowUtc)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(new X500DistinguishedName($"CN={subjectName.Replace(",", " ", StringComparison.Ordinal)}"), key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        var notBefore = nowUtc.AddMinutes(-10);
        using var certificate = request.CreateSelfSigned(notBefore, nowUtc.Add(lifetime));
        var pkcs12 = certificate.Export(X509ContentType.Pkcs12);
        try
        {
            return new Created(Prefix + Convert.ToBase64String(pkcs12), certificate.Export(X509ContentType.Cert), certificate.Thumbprint,
                certificate.NotAfter.ToUniversalTime());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs12);
        }
    }

    /// <summary>Loads a stored certificate (with its private key) in memory only.</summary>
    public static X509Certificate2 Load(string stored)
    {
        if (!IsCertificate(stored)) throw new ArgumentException("The stored credential is not a certificate.", nameof(stored));
        var bytes = Convert.FromBase64String(stored[Prefix.Length..]);
        try
        {
            var flags = OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;
            return X509CertificateLoader.LoadPkcs12(bytes, null, flags);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>The base64 PKCS#12 handed to the read-only PowerShell collector through its standard input.</summary>
    public static string Pkcs12Base64(string stored) =>
        IsCertificate(stored) ? stored[Prefix.Length..] : throw new ArgumentException("The stored credential is not a certificate.", nameof(stored));
}
