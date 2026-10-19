using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Jellyfin.Plugin.Sso.E2eTests;

/// <summary>
/// One self-signed certificate for every identity provider in the suite, real or fake.
/// The plugin's discovery client refuses plain HTTP off loopback, and the providers are
/// reached under a name that is not loopback, so they all have to speak HTTPS. The PEM
/// files are what a container needs; the PKCS#12 is what Kestrel needs.
/// </summary>
public sealed class TestCertificate
{
    /// <summary>The single name every party uses: the containers resolve it to the Docker
    /// host, the browser is told to resolve it to loopback.</summary>
    public const string HostName = "host.docker.internal";

    private TestCertificate(string directory, X509Certificate2 certificate)
    {
        Certificate = certificate;
        CertificatePath = Path.Combine(directory, "idp.crt");
        PrivateKeyPath = Path.Combine(directory, "idp.key");

        Directory.CreateDirectory(directory);
        File.WriteAllText(CertificatePath, certificate.ExportCertificatePem());
        File.WriteAllText(PrivateKeyPath, certificate.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());
    }

    public X509Certificate2 Certificate { get; }

    /// <summary>PEM certificate, mounted into the server container as a trusted CA.</summary>
    public string CertificatePath { get; }

    /// <summary>PEM private key, for providers that terminate TLS themselves.</summary>
    public string PrivateKeyPath { get; }

    public static TestCertificate Create(string directory)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=jellyfin-sso-e2e", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(certificateAuthority: false, false, 0, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));

        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(HostName);
        names.AddDnsName("localhost");
        names.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        // Reload so the private key stays attached to the instance Kestrel serves.
        return new TestCertificate(
            directory,
            X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), password: null));
    }

    /// <summary>
    /// A free TCP port for something that has to be published on a known port. Chosen from
    /// a fixed window below the ephemeral range, because that range is where Docker hands
    /// out published ports — taking one from there collides with a container that already
    /// has it, and the symptom (a different container suddenly unreachable) is miserable
    /// to debug.
    /// </summary>
    public static int FreePort()
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = Random.Shared.Next(20_000, 30_000);
            try
            {
                using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, candidate);
                listener.Start();
                listener.Stop();
                return candidate;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Taken; try another.
            }
        }

        throw new InvalidOperationException("Found no free port between 20000 and 30000.");
    }
}
