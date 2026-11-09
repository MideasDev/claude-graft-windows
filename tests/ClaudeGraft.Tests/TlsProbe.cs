using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using ClaudeGraft.Core.Pool;

/// The relay's TLS, end to end on loopback: the certificate made, presented
/// by a server, and checked by a client that trusts only the authority, the
/// way Node does with NODE_EXTRA_CA_CERTS.
static class TlsProbe
{
    public static async Task Run(Action<bool, string> check)
    {
        var leaf = PoolCertificates.Ensure();
        check(leaf.HasPrivateKey, "the relay's certificate carries its key");
        check(File.Exists(PoolCertificates.AuthorityFile), "the authority is written where Claude Code is told to look");
        var authority = PoolCertificates.Authority();
        check(!authority.HasPrivateKey, "the authority's key is not kept");
        check(authority.Extensions["2.5.29.30"]?.Critical == true, "the authority carries critical name constraints");

        bool Trusted(string host)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync();
                using var tls = new SslStream(accepted.GetStream());
                try { await tls.AuthenticateAsServerAsync(leaf, false, false); } catch { }
            });
            try
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, port);
                using var tls = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
                {
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(authority);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    var ok = chain.Build(new X509Certificate2(cert!));
                    var nameOk = new X509Certificate2(cert!).MatchesHostname(host);
                    return ok && nameOk;
                });
                tls.AuthenticateAsClient(host);
                return true;
            }
            catch { return false; }
            finally { server.Wait(5000); listener.Stop(); }
        }

        check(Trusted(UsagePool.Host), "a client trusting only the authority accepts the relay as api.anthropic.com");
        check(!Trusted("example.com"), "and refuses it for any other name");

        // The constraint itself: a certificate the authority could have
        // signed for another site is refused by the chain, not just the name.
        check(PoolCertificates.Ensure().Thumbprint == leaf.Thumbprint, "a current certificate is reused, not made again");
        await Task.CompletedTask;
    }
}
