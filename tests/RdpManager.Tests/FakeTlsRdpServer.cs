using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RdpManager.Tests;

/// <summary>
/// Server RDP finto su 127.0.0.1: risponde alla negoziazione X.224 scegliendo TLS e presenta un
/// certificato autofirmato creato in memoria (non installato in alcun archivio). Basta perché il
/// controllo RDP mostri l'avviso "identità del computer remoto non verificabile".
/// </summary>
internal sealed class FakeTlsRdpServer : IDisposable
{
    private readonly TcpListener _l = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _cert;
    private readonly Action<string> _log;
    public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;

    public const byte ProtocolTls = 1, ProtocolCredSsp = 2;
    private readonly byte _protocol;
    public int Connections;

    /// <param name="protocol">Protocollo scelto nella risposta: TLS semplice (avviso certificato nel
    /// processo) o CredSSP/NLA come un Windows reale (credenziali richieste prima del certificato).</param>
    public FakeTlsRdpServer(Action<string> log, byte protocol = ProtocolTls)
    {
        _log = log;
        _protocol = protocol;
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=finto-rdp", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var eph = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        _cert = new X509Certificate2(eph.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
        _l.Start();
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (true)
        {
            TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(); } catch { return; }
            _ = Task.Run(() => Handle(c));
        }
    }

    private async Task Handle(TcpClient c)
    {
        try
        {
            Interlocked.Increment(ref Connections);
            var s = c.GetStream();
            var hdr = new byte[4];
            await s.ReadExactlyAsync(hdr);
            int len = hdr[2] << 8 | hdr[3];
            var body = new byte[len - 4];
            await s.ReadExactlyAsync(body);
            _log($"server: X.224 CR {len} byte, protocolli richiesti=0x{(body.Length >= 4 ? BitConverter.ToUInt32(body, body.Length - 4) : 0):X}");
            byte[] cc = { 3, 0, 0, 19, 14, 0xD0, 0, 0, 0x12, 0x34, 0, 2, 0, 8, 0, _protocol, 0, 0, 0 }; // X.224 CC + RDP_NEG_RSP
            await s.WriteAsync(cc);
            var ssl = new SslStream(s, false);
            await ssl.AuthenticateAsServerAsync(_cert, false, SslProtocols.Tls12, false);
            _log("server: TLS completato, resto in attesa");
            var buf = new byte[4096];
            while (await ssl.ReadAsync(buf) > 0) { }
            _log("server: client chiuso");
        }
        catch (Exception ex) { _log("server: " + ex.GetType().Name + " " + ex.Message); }
        finally { c.Dispose(); }
    }

    public void Dispose() { _l.Stop(); _cert.Dispose(); }
}
