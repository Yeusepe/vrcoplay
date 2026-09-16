// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
namespace VRCoplay;
internal sealed class RemotePlayHostException(string summary, string detail, string? diagnosticPath)
    : IOException(summary + " " + detail + (diagnosticPath is null ? " Host diagnostic log could not be saved." : $" Host log: {Path.GetFileName(diagnosticPath)}"))
{
    internal string PlayerMessage { get; } = summary + " Ask the host to check the remote-play log.";
    internal string? DiagnosticPath { get; } = diagnosticPath;
}
internal sealed class SunshineStartupDiagnostics
{
    private const int MaxCharacters = 128 * 1024;
    private static readonly string[] CredentialWords =
        ["password", "authorization", "privatekey", "private_key", "pairing_id", "client_cert", "salt =", "pin ="];
    private readonly object _gate = new();
    private readonly Queue<(string Source, string Text)> _lines = new();
    private int _characters;
    private bool _pem;
    internal string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    internal string Stage { get; set; } = "preparing host files";
    internal int? ExitCode { get; set; }
    internal string? LastApiError { get; set; }
    internal void Record(string source, string line)
    {
        lock (_gate)
        {
            if (line.Contains("-----BEGIN ", StringComparison.Ordinal)) _pem = true;
            if (_pem)
            {
                if (line.Contains("-----END ", StringComparison.Ordinal)) _pem = false;
                return;
            }
            if (CredentialWords.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase)))
                line = "[credential diagnostic omitted]";
            if (line.Length > 4096) line = line[..4096] + " [truncated]";
            _lines.Enqueue((source, line));
            _characters += line.Length + source.Length + 4;
            while (_characters > MaxCharacters && _lines.TryDequeue(out var old))
                _characters -= old.Text.Length + old.Source.Length + 4;
        }
    }
    internal string StandardOutput
    {
        get { lock (_gate) return string.Join('\n', _lines.Where(x => x.Source == "stdout").Select(x => x.Text)); }
    }
    internal RemotePlayHostException Failure(Exception error, string display, int port, string? directory = null)
    {
        var code = ExitCode is { } exit ? $" Sunshine exited with code {exit} (0x{exit:X8})." : "";
        var reason = error is OperationCanceledException ? " Startup timed out after 90 seconds."
            : ExitCode is null ? $" {error.GetType().Name} (0x{error.HResult:X8})." : "";
        var message = Redact($"Remote play failed on the host while {Stage}.{code}{reason} Diagnostic {Id}.");
        Record("failure", $"{error.GetType().FullName} (0x{error.HResult:X8}): {error.Message}");
        if (error.StackTrace is { } stack) Record("stack", stack);
        if (LastApiError is { } apiError) Record("last API error", apiError);
        var report = new StringBuilder()
            .AppendLine($"VRCoplay remote-play startup diagnostic {Id}")
            .AppendLine($"UTC: {DateTimeOffset.UtcNow:O}")
            .AppendLine($"Windows: {Environment.OSVersion}; 64-bit process: {Environment.Is64BitProcess}")
            .AppendLine($"Stage: {Redact(Stage)}")
            .AppendLine($"Display: {Redact(display)}")
            .AppendLine($"Base port: {port}")
            .AppendLine($"Exit code: {(ExitCode is { } value ? $"{value} (0x{value:X8})" : "not observed before cleanup")}")
            .AppendLine();
        lock (_gate)
            foreach (var line in _lines) report.AppendLine($"[{Redact(line.Source)}] {Redact(line.Text)}");
        string detail;
        lock (_gate)
        {
            var output = _lines.Where(x => x.Source is "stdout" or "stderr").Select(x => x.Text).ToArray();
            detail = (ExitCode is null ? null : output.LastOrDefault(x => x.Contains("Fatal:", StringComparison.OrdinalIgnoreCase))
                ?? output.LastOrDefault(x => x.Contains("Error:", StringComparison.OrdinalIgnoreCase)))
                ?? _lines.Last(x => x.Source == "failure").Text;
        }
        detail = Redact(detail);
        if (detail.Length > 500) detail = detail[..500] + "…";
        try
        {
            directory ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCoplay", "Logs", "RemotePlay");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"sunshine-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Id}.log");
            File.WriteAllText(path, report.ToString());
            return new(message, detail, path);
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException)
        {
            return new(message, detail, null);
        }
    }
    private static readonly Regex LocalPaths = new(
        "(?<![\\p{L}\\p{N}_])(?:[A-Za-z]:[\\\\/]|\\\\\\\\|/(?:Users|home)/)[^\\r\\n\"'<>|]*",
        RegexOptions.CultureInvariant);
    internal static string Redact(string text)
    {
        text = LocalPaths.Replace(text, "[local path]");
        foreach (var identity in new[] { Environment.UserName, Environment.MachineName })
            if (!string.IsNullOrEmpty(identity))
                text = Regex.Replace(text, "(?<![\\p{L}\\p{N}_])" + Regex.Escape(identity) + "(?![\\p{L}\\p{N}_])",
                    "[local identity]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return text;
    }
}
