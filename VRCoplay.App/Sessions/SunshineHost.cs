// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using CliWrap;
namespace VRCoplay;
internal sealed record SunshinePlayer(string Id, RemotePlayCredentials Credentials)
{
    public override string ToString() => "Sunshine player";
}
internal sealed class SunshineHost : IAsyncDisposable
{
    internal const string Application = "VRCoplay";
    private static readonly HttpClient Downloads = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly PortableToolSetup Setup = new(Downloads,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCoplay", "Sunshine"),
        () => null, new("2026.906.222525",
            new("https://github.com/LizardByte/Sunshine/releases/download/v2026.906.222525/Sunshine-Windows-AMD64-lite.zip"),
            "50f4123dd15a6817513589c912581260c34cc4a58e9a5f3071ebac3d92c94c15", "Sunshine/sunshine.exe", "Sunshine"), portableProfile: false);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _pairing = new(1, 1);
    private readonly string _directory = PrivateSessionDirectory.Create("Hosts");
    private readonly X509Certificate2 _certificate;
    private readonly HttpClient _api;
    private readonly SunshineStartupDiagnostics _diagnostics = new();
    private CommandTask<CommandResult>? _process;
    private CancellationTokenSource? _processStop;
    private int _disposed;
    internal int Port { get; } = FindPort();
    internal bool Running => !_stop.IsCancellationRequested && _process is { Task.IsCompleted: false };
    internal Task Completion => _process?.Task ?? Task.CompletedTask;
    private SunshineHost()
    {
        using var key = RSA.Create(2048);
        _certificate = new CertificateRequest("CN=VRCoplay Host", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllText(Path.Combine(_directory, "server.crt"), _certificate.ExportCertificatePem());
        File.WriteAllText(Path.Combine(_directory, "server.key"), key.ExportPkcs8PrivateKeyPem());
        _api = new(GameStreamPairing.PinnedHandler(_certificate))
            { BaseAddress = new($"https://127.0.0.1:{Port + 1}"), Timeout = Timeout.InfiniteTimeSpan };
    }
    internal static async Task<SunshineHost> StartAsync(string display, IProgress<string>? progress,
        CancellationToken stop, string? executable = null, bool loopbackOnly = false)
    {
        if (string.IsNullOrWhiteSpace(display) || display.Any(char.IsControl))
            throw new ArgumentException("Choose a display for remote play.");
        executable ??= (await Setup.EnsureAsync(progress, stop).ConfigureAwait(false)).Executable;
        var host = new SunshineHost();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop, host._stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            progress?.Report("Preparing remote play…");
            await File.WriteAllTextAsync(Path.Combine(host._directory, "apps.json"),
                JsonSerializer.Serialize(new { env = new { }, apps = new[] { new Dictionary<string, string>
                    { ["name"] = Application, ["image-path"] = "desktop.png" } } }), deadline.Token).ConfigureAwait(false);
            host._diagnostics.Record("setup", $"Sunshine executable: {executable}");
            host._diagnostics.Stage = "discovering host displays";
            await host.LaunchAsync(executable, "", true, deadline.Token).ConfigureAwait(false);
            host._diagnostics.Stage = "selecting the host display";
            var displayId = ReadDisplayId(host._diagnostics.StandardOutput, display);
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            host._diagnostics.Stage = "configuring host authentication";
            await host.ApiAsync("/api/password", new { newUsername = "vrcoplay", newPassword = password, confirmNewPassword = password }, deadline.Token).ConfigureAwait(false);
            host._api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes("vrcoplay:" + password)));
            await host.StopProcessAsync().ConfigureAwait(false);
            host._diagnostics.Stage = "starting the selected host display";
            await host.LaunchAsync(executable, displayId, loopbackOnly, deadline.Token).ConfigureAwait(false);
            return host;
        }
        catch (Exception error) when (!stop.IsCancellationRequested)
        {
            await host.StopProcessAsync().ConfigureAwait(false);
            var failure = host._diagnostics.Failure(error, display, host.Port);
            await host.DisposeAsync().ConfigureAwait(false);
            throw failure;
        }
        catch { await host.DisposeAsync().ConfigureAwait(false); throw; }
    }
    private async Task LaunchAsync(string executable, string displayId, bool loopback, CancellationToken stop)
    {
        string FilePath(string name) => Path.Combine(_directory, name).Replace('\\', '/');
        var config = $"""
            sunshine_name = VRCoplay
            port = {Port}
            {(loopback ? "bind_address = 127.0.0.1" : "")}
            address_family = {(loopback ? "ipv4" : "both")}
            origin_web_ui_allowed = pc
            upnp = disabled
            system_tray = disabled
            keyboard = disabled
            mouse = disabled
            controller = disabled
            lan_encryption_mode = 2
            wan_encryption_mode = 2
            {(displayId.Length == 0 ? "" : $"output_name = {displayId}")}
            dd_configuration_option = {(displayId.Length == 0 ? "disabled" : "verify_only")}
            dd_resolution_option = disabled
            dd_refresh_rate_option = disabled
            dd_hdr_option = disabled
            nvenc_opengl_vulkan_on_dxgi = disabled
            install_steam_audio_drivers = disabled
            hevc_mode = 1
            av1_mode = 1
            min_log_level = info
            file_apps = {FilePath("apps.json")}
            file_state = {FilePath("state.json")}
            credentials_file = {FilePath("credentials.json")}
            pkey = {FilePath("server.key")}
            cert = {FilePath("server.crt")}
            log_path = {FilePath("sunshine.log")}
            """;
        var path = Path.Combine(_directory, "sunshine.conf");
        await File.WriteAllTextAsync(path, config, stop).ConfigureAwait(false);
        _diagnostics.LastApiError = null;
        _diagnostics.Record("launch", $"{_diagnostics.Stage}; output={displayId}; loopback={loopback}; port={Port}");
        _processStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _process = Cli.Wrap(executable).WithWorkingDirectory(Path.GetDirectoryName(executable)!)
            .WithArguments([path]).WithValidation(CommandResultValidation.None)
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line => _diagnostics.Record("stdout", line)))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line => _diagnostics.Record("stderr", line))).ExecuteOwnedAsync(_processStop.Token);
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            if (_process.Task.IsCompleted)
            {
                var result = await _process.Task.ConfigureAwait(false);
                _diagnostics.ExitCode = result.ExitCode;
                throw new IOException("Sunshine ended before its host API became ready.");
            }
            try
            {
                using var request = CancellationTokenSource.CreateLinkedTokenSource(stop);
                request.CancelAfter(TimeSpan.FromSeconds(2));
                await ApiAsync("/api/configLocale", null, request.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (!stop.IsCancellationRequested && error is HttpRequestException or OperationCanceledException)
            {
                _diagnostics.LastApiError = $"{error.GetType().Name}: {error.Message}";
            }
            await Task.Delay(200, stop).ConfigureAwait(false);
        }
    }
    internal async Task<SunshinePlayer> PairAsync(CancellationToken stop)
    {
        using var transaction = CancellationTokenSource.CreateLinkedTokenSource(stop, _stop.Token);
        transaction.CancelAfter(TimeSpan.FromSeconds(45));
        await _pairing.WaitAsync(transaction.Token).ConfigureAwait(false);
        var name = "VRCoplay-" + Guid.NewGuid().ToString("N");
        string? pendingId = null;
        try
        {
            if (!Running) throw new IOException("Remote play has stopped on the host.");
            var identity = await GameStreamPairing.PairAsync(Port, _certificate, name, async (deviceName, pin, cancel) =>
            {
                while (pendingId is null)
                {
                    var waiting = await ApiAsync("/api/pin", null, cancel).ConfigureAwait(false);
                    foreach (var entry in waiting.GetProperty("pairings").EnumerateArray())
                        if (entry.GetProperty("name").GetString() == deviceName
                            && IPAddress.TryParse(entry.GetProperty("address").GetString(), out var address) && IPAddress.IsLoopback(address))
                            pendingId = entry.GetProperty("id").GetString();
                    if (pendingId is null) await Task.Delay(100, cancel).ConfigureAwait(false);
                }
                await ApiAsync("/api/pin", new { pairing_id = pendingId, pin, name }, cancel).ConfigureAwait(false);
            }, transaction.Token).ConfigureAwait(false);
            var client = await FindClientAsync(name, transaction.Token).ConfigureAwait(false)
                ?? throw new IOException("The remote play client was not authorized.");
            return new(client, identity);
        }
        catch
        {
            transaction.Cancel();
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (pendingId is not null)
                    await ApiAsync("/api/pin", new { pairing_id = pendingId }, cleanup.Token, HttpMethod.Delete).ConfigureAwait(false);
                if (await FindClientAsync(name, cleanup.Token).ConfigureAwait(false) is { } id)
                    await RevokeAsync(id).ConfigureAwait(false);
            }
            catch { _stop.Cancel(); }
            throw;
        }
        finally { transaction.Cancel(); _pairing.Release(); }
    }
    private async Task<string?> FindClientAsync(string name, CancellationToken stop)
    {
        var clients = await ApiAsync("/api/clients/list", null, stop).ConfigureAwait(false);
        return clients.GetProperty("named_certs").EnumerateArray()
            .Where(x => x.GetProperty("name").GetString() == name).Select(x => x.GetProperty("uuid").GetString()).SingleOrDefault();
    }
    internal async Task RevokeAsync(string id)
    {
        if (!Running) return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ApiAsync("/api/clients/update", new { uuid = id, enabled = false }, deadline.Token).ConfigureAwait(false);
            await ApiAsync("/api/clients/unpair", new { uuid = id }, deadline.Token).ConfigureAwait(false);
        }
        catch when (_stop.IsCancellationRequested) { }
        catch { _stop.Cancel(); throw new IOException("Remote play stopped because player access could not be revoked."); }
    }
    private async Task<JsonElement> ApiAsync(string path, object? body, CancellationToken stop, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? (body is null ? HttpMethod.Get : HttpMethod.Post), path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _api.SendAsync(request, stop).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(stop).ConfigureAwait(false);
        if (result.TryGetProperty("status", out var success) && success.ValueKind == JsonValueKind.False)
            throw new IOException("The remote play host rejected the operation.");
        return result;
    }
    internal static string ReadDisplayId(string log, string display)
    {
        const string marker = "Currently available display devices:";
        var index = log.IndexOf(marker, StringComparison.Ordinal);
        var start = index < 0 ? -1 : log.IndexOf('[', index + marker.Length);
        if (start >= 0)
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(log[start..]));
            using var devices = JsonDocument.ParseValue(ref reader);
            foreach (var device in devices.RootElement.EnumerateArray())
                if (device.GetProperty("display_name").GetString() == display
                    && device.GetProperty("info").ValueKind == JsonValueKind.Object
                    && device.GetProperty("device_id").GetString() is { } id && Guid.TryParse(id, out _)) return id;
        }
        throw new IOException("The selected display is unavailable for remote play. Choose a connected display.");
    }
    private static int FindPort()
    {
        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var used = properties.GetActiveTcpListeners().Concat(properties.GetActiveUdpListeners()).Select(x => x.Port).ToHashSet();
        for (var i = 0; i < 100; i++)
        {
            var port = RandomNumberGenerator.GetInt32(48000, 60000);
            if (new[] { -5, 0, 1, 9, 10, 11, 13, 21 }.All(offset => !used.Contains(port + offset))) return port;
        }
        throw new IOException("No free ports are available for remote play.");
    }
    private async Task StopProcessAsync()
    {
        if (_processStop is null) return;
        _processStop.Cancel();
        if (_process is not null) await ((Task)_process.Task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _processStop.Dispose();
        _processStop = null;
        _process = null;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        await _pairing.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopProcessAsync().ConfigureAwait(false);
            _api.Dispose();
            _certificate.Dispose();
            PrivateSessionDirectory.Delete(_directory);
        }
        finally { _pairing.Release(); }
    }
}
