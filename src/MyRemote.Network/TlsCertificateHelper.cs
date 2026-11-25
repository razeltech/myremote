using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MyRemote.Network;

/// <summary>
/// Generates an ephemeral in-memory ECDSA P-256 self-signed certificate for TLS 1.2/1.3 peer transport.
/// </summary>
public static class TlsCertificateHelper
{
    public static X509Certificate2 CreateEphemeralServerCertificate(string deviceId)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(
            $"CN=MyRemote-{deviceId}",
            ecdsa,
            HashAlgorithmName.SHA256);

        var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(30));

        // On Windows SslStream requires the private key to be persisted in an exportable/ephemeral PFX for Schannel.
        var pfxBytes = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfxBytes, password: null, X509KeyStorageFlags.Exportable);
    }
}
