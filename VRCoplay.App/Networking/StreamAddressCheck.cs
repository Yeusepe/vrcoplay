// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net.Sockets;
using System.Text;
namespace VRCoplay;
internal static class StreamAddressCheck
{
    internal static async Task<bool> ReadyAsync(string link, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var uri = new Uri(link);
            if (uri.Scheme == "https" && StreamLink.StreamId(link) is not null)
            {
                using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                return response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType == "text/html";
            }
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync(
                Encoding.ASCII.GetBytes(
                    $"DESCRIBE {link.Replace("rtspt://", "rtsp://", StringComparison.Ordinal)} RTSP/1.0\r\nCSeq: 1\r\n\r\n"
                ),
                timeout.Token
            );
            using var reader = new StreamReader(stream, Encoding.ASCII);
            return (await reader.ReadLineAsync(timeout.Token))?.StartsWith("RTSP/1.0 302 ", StringComparison.Ordinal)
                == true;
        }
        catch
        {
            stop.ThrowIfCancellationRequested();
            return false;
        }
    }
}
