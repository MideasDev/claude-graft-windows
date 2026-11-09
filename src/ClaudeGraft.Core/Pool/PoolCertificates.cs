using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClaudeGraft.Core.Pool;

/// The authority the relay presents to Claude Code, and the one certificate
/// it signs. The authority carries a critical `nameConstraints` permitting
/// `api.anthropic.com` alone, and its private key never touches the disk:
/// it signs the one leaf and is thrown away, so what Node is told to trust
/// cannot vouch for any other site, for anybody.
///
/// The leaf and its key are kept sealed with this Windows user's DPAPI, the
/// way Claude's own key is. Windows' TLS (SChannel) refuses a key held in
/// memory alone ("the platform does not support ephemeral keys", measured),
/// so the relay loads it into the user's own key store without persisting
/// it: Windows deletes that copy when the certificate is released. This is
/// the one place the port differs from macOS's `kSecImportToMemoryOnly`.
public static class PoolCertificates
{
    public static string Directory => Path.Combine(Paths.GraftHome, "pool");
    public static string AuthorityFile => Path.Combine(Directory, "relay-ca.pem");
    static string IdentityFile => Path.Combine(Directory, "relay-identity.bin");

    /// A leaf is made again when it has less than this left. Only as a relay
    /// starts: a session already running trusts what it read when it began.
    static readonly TimeSpan Renewal = TimeSpan.FromDays(60);

    const X509KeyStorageFlags Flags = X509KeyStorageFlags.UserKeySet;

    public static bool IsPresent => File.Exists(AuthorityFile) && File.Exists(IdentityFile);

    public static X509Certificate2 Ensure()
    {
        System.IO.Directory.CreateDirectory(Directory);
        using var gate = new Mutex(false, @"Local\ClaudeGraftWindows.Certificates");
        gate.WaitOne(TimeSpan.FromSeconds(30));
        try
        {
            if (IsPresent && Load() is { } current && current.NotAfter - DateTime.Now > Renewal) return current;
            return Make();
        }
        finally { gate.ReleaseMutex(); }
    }

    static X509Certificate2? Load()
    {
        try
        {
            var sealedIdentity = File.ReadAllBytes(IdentityFile);
            return X509CertificateLoader.LoadPkcs12(Dpapi.Unprotect(sealedIdentity), null, Flags);
        }
        catch (Exception e)
        {
            Diagnostics.Note("relay.identity-unreadable", e.Message);
            return null;
        }
    }

    static X509Certificate2 Make()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var authorityKey = RSA.Create(2048);
        var authorityRequest = new CertificateRequest(
            "CN=Claude Graft relay for " + UsagePool.Host, authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        authorityRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        authorityRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(authorityRequest.PublicKey, false));
        authorityRequest.CertificateExtensions.Add(NameConstraints(UsagePool.Host));
        using var authority = authorityRequest.CreateSelfSigned(now, now.AddYears(10));

        using var leafKey = RSA.Create(2048);
        var leafRequest = new CertificateRequest("CN=" + UsagePool.Host, leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(UsagePool.Host);
        leafRequest.CertificateExtensions.Add(names.Build());
        leafRequest.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(authority, true, false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var signed = leafRequest.Create(authority, now, now.AddDays(800), serial);
        using var leaf = signed.CopyWithPrivateKey(leafKey);

        // The authority's key goes out of scope here and is never written.
        var pfx = leaf.Export(X509ContentType.Pkcs12);
        var pem = authority.ExportCertificatePem() + "\n";

        var temp = IdentityFile + ".tmp";
        File.WriteAllBytes(temp, Dpapi.Protect(pfx));
        File.Move(temp, IdentityFile, overwrite: true);
        File.WriteAllText(AuthorityFile + ".tmp", pem);
        File.Move(AuthorityFile + ".tmp", AuthorityFile, overwrite: true);
        Diagnostics.Note("relay.certificate-made", $"until {leaf.NotAfter:yyyy-MM-dd}");
        return X509CertificateLoader.LoadPkcs12(pfx, null, Flags);
    }

    /// `nameConstraints` (2.5.29.30), critical: permitted subtree DNS:<host>.
    static X509Extension NameConstraints(string host)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        using (writer.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))
        using (writer.PushSequence())
            writer.WriteCharacterString(UniversalTagNumber.IA5String, host, new Asn1Tag(TagClass.ContextSpecific, 2));
        return new X509Extension("2.5.29.30", writer.Encode(), critical: true);
    }

    public static X509Certificate2 Authority() => X509CertificateLoader.LoadCertificate(
        Convert.FromBase64String(string.Concat(File.ReadAllLines(AuthorityFile).Where(l => !l.StartsWith("-----")))));
}
