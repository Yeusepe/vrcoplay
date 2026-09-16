// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VRCoplay;
sealed class StreamLinkResolver(IConfiguration config, StreamTickets tickets) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var address = IPEndPoint.Parse(config["RESOLVER_LISTEN"] ?? "0.0.0.0:8555");
        using var listener = new TcpListener(address);
        listener.Start(64);
        await Parallel.ForAsync(
            0,
            64,
            new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = stoppingToken },
            async (_, stop) =>
            {
                while (!stop.IsCancellationRequested)
                    await ReplyAsync(await listener.AcceptTcpClientAsync(stop), stop);
            }
        );
    }
    private async Task ReplyAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            var stream = client.GetStream();
            var reader = PipeReader.Create(stream);
            try
            {
                for (var request = 0; request < 4; )
                {
                    var read = await reader.ReadAsync(deadline.Token);
                    var buffer = read.Buffer;
                    var framing = new SequenceReader<byte>(buffer.Slice(0, Math.Min(buffer.Length, 4096)));
                    if (!framing.TryReadTo(out ReadOnlySequence<byte> header, "\r\n\r\n"u8))
                    {
                        var finished = read.IsCompleted || buffer.Length >= 4096;
                        reader.AdvanceTo(buffer.Start, buffer.End);
                        if (finished)
                            return;
                        continue;
                    }
                    var lines = Encoding.ASCII.GetString(header).Split("\r\n");
                    reader.AdvanceTo(framing.Position);
                    request++;
                    var first = lines[0].Split(' ');
                    var sequence = lines
                        .FirstOrDefault(x => x.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase))
                        ?[5..].Trim();
                    if (!int.TryParse(sequence, out var cseq))
                        cseq = 0;
                    var method = first[0];
                    var destination =
                        method == "DESCRIBE"
                        && first.Length == 3
                        && first[2] == "RTSP/1.0"
                        && Uri.TryCreate(first[1], UriKind.Absolute, out var uri)
                        && uri.Query.Length == 0
                        && uri.Fragment.Length == 0
                            ? tickets.Resolve(uri.AbsolutePath)
                            : null;
                    var response = method switch
                    {
                        "OPTIONS" => "200 OK\r\nPublic: OPTIONS, DESCRIBE\r\n",
                        "DESCRIBE" when destination is not null =>
                            $"302 Moved Temporarily\r\nLocation: {destination}\r\n",
                        "DESCRIBE" => "404 Not Found\r\n",
                        _ => "405 Method Not Allowed\r\n",
                    };
                    await stream.WriteAsync(
                        Encoding.ASCII.GetBytes($"RTSP/1.0 {response}CSeq: {cseq}\r\nContent-Length: 0\r\n\r\n"),
                        deadline.Token
                    );
                    if (method != "OPTIONS")
                        return;
                }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException) { }
            finally
            {
                await reader.CompleteAsync();
            }
        }
    }
}
