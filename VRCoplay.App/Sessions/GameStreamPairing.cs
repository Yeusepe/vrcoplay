// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml;
using System.Xml.Linq;
namespace VRCoplay;
internal static class GameStreamPairing
{
    internal static async Task<RemotePlayCredentials> PairAsync(int port, X509Certificate2 server,
        string deviceName, Func<string, string, CancellationToken, Task> approve, CancellationToken stop)
    {
        using var key = RSA.Create(2048);
        using var cert = new CertificateRequest("CN=VRCoplay Player", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var certificate = cert.ExportCertificatePem() + "\n";
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        using var transportCert = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        using var client = new HttpClient(PinnedHandler(server, transportCert)) { Timeout = Timeout.InfiniteTimeSpan };
        var pin = RandomNumberGenerator.GetInt32(10000).ToString("D4");
        var salt = RandomNumberGenerator.GetBytes(16);
        var aesKey = SHA256.HashData([.. salt, .. Encoding.ASCII.GetBytes(pin)])[..16];
        using var aes = Aes.Create();
        aes.Key = aesKey;
        var prefix = $"uniqueid={id}&uuid={Guid.NewGuid():N}&devicename={deviceName}&updateState=1&";
        var first = ReadAsync("pair", "phrase=getservercert&salt=" + Convert.ToHexString(salt) +
            "&clientcert=" + Convert.ToHexString(Encoding.ASCII.GetBytes(certificate)));
        var approval = approve(deviceName, pin, stop);
        try
        {
            if (await Task.WhenAny(first, approval).ConfigureAwait(false) == approval)
                await approval.ConfigureAwait(false);
            var hello = await first.ConfigureAwait(false);
            using var received = X509Certificate2.CreateFromPem(Encoding.ASCII.GetString(Hex(hello, "plaincert")));
            if (!received.RawData.AsSpan().SequenceEqual(server.RawData))
                throw new CryptographicException("The remote play server certificate changed.");
            var challenge = RandomNumberGenerator.GetBytes(16);
            var reply = await ReadAsync("pair", "clientchallenge=" +
                Convert.ToHexString(aes.EncryptEcb(challenge, PaddingMode.None))).ConfigureAwait(false);
            var response = aes.DecryptEcb(Hex(reply, "challengeresponse"), PaddingMode.None);
            if (response.Length != 48) throw new InvalidDataException("Invalid pairing challenge.");
            var secret = RandomNumberGenerator.GetBytes(16);
            var proof = SHA256.HashData([.. response[32..], .. Signature(cert), .. secret]);
            reply = await ReadAsync("pair", "serverchallengeresp=" +
                Convert.ToHexString(aes.EncryptEcb(proof, PaddingMode.None))).ConfigureAwait(false);
            var serverProof = Hex(reply, "pairingsecret");
            if (serverProof.Length != 272) throw new InvalidDataException("Invalid pairing proof.");
            using var publicKey = server.GetRSAPublicKey()!;
            if (!publicKey.VerifyData(serverProof[..16], serverProof[16..], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                || !CryptographicOperations.FixedTimeEquals(response[..32],
                    SHA256.HashData([.. challenge, .. Signature(server), .. serverProof[..16]])))
                throw new CryptographicException("The remote play pairing proof could not be verified.");
            await ReadAsync("pair", "clientpairingsecret=" + Convert.ToHexString([
                .. secret, .. key.SignData(secret, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)])).ConfigureAwait(false);
            await ReadAsync("pair", "phrase=pairchallenge", secure: true).ConfigureAwait(false);
            await approval.ConfigureAwait(false);
            var info = await ReadAsync("serverinfo", "").ConfigureAwait(false);
            var apps = await ReadAsync("applist", "", secure: true).ConfigureAwait(false);
            var app = apps.Elements("App").Single(x => Value(x, "AppTitle") == SunshineHost.Application);
            return new(id, certificate, key.ExportPkcs8PrivateKeyPem() + "\n", server.ExportCertificatePem() + "\n",
                Value(info, "uniqueid"), port, int.Parse(Value(app, "ID")));
        }
        finally
        {
            _ = first.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _ = approval.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        async Task<XElement> ReadAsync(string path, string query, bool secure = false)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{(secure ? "https" : "http")}://127.0.0.1:{(secure ? port - 5 : port)}/{path}?{prefix}{query}");
            using var message = await client.SendAsync(request, stop).ConfigureAwait(false);
            message.EnsureSuccessStatusCode();
            var xml = await message.Content.ReadAsStringAsync(stop).ConfigureAwait(false);
            using var reader = XmlReader.Create(new StringReader(xml), new()
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 65536 });
            var root = XElement.Load(reader);
            if ((string?)root.Attribute("status_code") != "200" || path == "pair" && Value(root, "paired") != "1")
                throw new InvalidOperationException("The remote play server rejected pairing. Try Play again.");
            return root;
        }
    }
    internal static SocketsHttpHandler PinnedHandler(X509Certificate2 server, X509Certificate2? client = null)
    {
        var expected = server.RawData;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            SslOptions = new()
            {
                AllowTlsResume = false,
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert is not null && cert.GetRawCertData().AsSpan().SequenceEqual(expected),
            },
        };
        if (client is not null) handler.SslOptions.ClientCertificates = new X509CertificateCollection { client };
        return handler;
    }
    private static string Value(XElement root, string name) => root.Element(name)?.Value
        ?? throw new InvalidDataException("The pairing response is incomplete.");
    private static byte[] Hex(XElement root, string name) => Convert.FromHexString(Value(root, name));
    private static byte[] Signature(X509Certificate2 cert)
    {
        var sequence = new AsnReader(cert.RawData, AsnEncodingRules.DER).ReadSequence();
        sequence.ReadEncodedValue();
        sequence.ReadEncodedValue();
        return sequence.ReadBitString(out _);
    }
}
