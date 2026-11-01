using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Matgate.Agent;

// The agent's TLS certificate: the configured .pfx, or a self-signed one made on the first start.
public static class AgentCertificate
{
    public static X509Certificate2 LoadOrCreate(AgentConfig config, string directory)
    {
        var path = Path.IsPathRooted(config.CertificatePath)
            ? config.CertificatePath
            : Path.Combine(directory, config.CertificatePath);
        if (File.Exists(path))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, config.CertificatePassword);
        }

        using var key = RSA.Create(3072);
        var request = new CertificateRequest($"CN={Environment.MachineName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(Environment.MachineName);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        File.WriteAllBytes(path, created.Export(X509ContentType.Pfx, config.CertificatePassword));
        return X509CertificateLoader.LoadPkcs12FromFile(path, config.CertificatePassword);
    }
}
