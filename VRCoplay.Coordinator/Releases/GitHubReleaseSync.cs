// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
public sealed partial class GitHubReleaseSync : BackgroundService
{
    private readonly string repository;
    private readonly string token;
    private readonly Uri baseUri;
    private readonly ReleaseStore store;
    private readonly ILogger<GitHubReleaseSync> logger;
    public GitHubReleaseSync(IConfiguration configuration, ILogger<GitHubReleaseSync> logger)
    {
        repository = configuration["RELEASE_GITHUB_REPOSITORY"] ?? "";
        token = configuration["RELEASE_GITHUB_TOKEN"] ?? "";
        baseUri = configuration["RELEASE_BASE_URI"] is { } releaseUri ? new(releaseUri) : new(VRCoplay.StreamLink.Server, "alpha/");
        var path = configuration["RELEASE_DIRECTORY"] ?? "";
        if (!RepositoryPattern().IsMatch(repository) || string.IsNullOrWhiteSpace(token) || !Path.IsPathFullyQualified(path) ||
            baseUri.Scheme != "https" || baseUri.Query.Length != 0 || baseUri.Fragment.Length != 0 || baseUri.UserInfo.Length != 0 || !baseUri.AbsolutePath.EndsWith('/'))
            throw new InvalidOperationException("Configure RELEASE_DIRECTORY, RELEASE_GITHUB_REPOSITORY, RELEASE_GITHUB_TOKEN and a stable HTTPS RELEASE_BASE_URI.");
        store = new(path);
        Directory.CreateDirectory(store.DirectoryPath);
        this.logger = logger;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
        EntityTagHeaderValue? entityTag = null;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(30);
            try { entityTag = await SynchronizeAsync(http, repository, token, baseUri, store, stoppingToken, entityTag); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                delay = TimeSpan.FromMinutes(5);
                logger.LogWarning("Release sync failed ({FailureType}); keeping the current release and retrying in five minutes.", error.GetType().Name);
            }
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
    public static async Task<EntityTagHeaderValue?> SynchronizeAsync(HttpClient http, string repository, string token, Uri baseUri, ReleaseStore store, CancellationToken cancellationToken, EntityTagHeaderValue? entityTag = null)
    {
        if (!RepositoryPattern().IsMatch(repository)) throw new InvalidDataException("Invalid release repository.");
        using var request = ApiRequest($"https://api.github.com/repos/{repository}/releases?per_page=100", token, binary: false);
        if (entityTag is not null) request.Headers.IfNoneMatch.Add(entityTag);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotModified) return entityTag;
        response.EnsureSuccessStatusCode();
        using var metadata = new MemoryStream();
        await CopyBoundedAsync(response.Content, metadata, 2 * 1024 * 1024, cancellationToken);
        metadata.Position = 0;
        using var releases = await JsonDocument.ParseAsync(metadata, cancellationToken: cancellationToken);
        JsonElement? latest = null;
        Version? version = null;
        foreach (var release in releases.RootElement.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean() || !release.TryGetProperty("published_at", out var published) || published.ValueKind != JsonValueKind.String) continue;
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            var candidate = tag.StartsWith("tester-v", StringComparison.Ordinal) ? ReleaseStore.ParseVersion(tag[8..]) : null;
            if (candidate is not null && (version is null || candidate > version)) { version = candidate; latest = release; }
        }
        if (latest is null || version is null || store.CurrentVersion() is { } current && current >= version) return response.Headers.ETag;
        var assets = latest.Value.GetProperty("assets").EnumerateArray().ToArray();
        var expected = ReleaseStore.Files(version);
        if (assets.Length != expected.Length || assets.Select(a => a.GetProperty("name").GetString()).Distinct().Count() != expected.Length ||
            assets.Any(a => !expected.Contains(a.GetProperty("name").GetString(), StringComparer.Ordinal)))
            throw new InvalidDataException("A release must contain exactly the distribution assets.");
        var staging = Path.Combine(store.DirectoryPath, ".download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (var asset in assets)
            {
                var name = asset.GetProperty("name").GetString()!;
                var id = asset.GetProperty("id").GetInt64();
                var size = asset.GetProperty("size").GetInt64();
                var limit = name == ReleaseStore.UnityPackageName ? 64L * 1024 * 1024 :
                    name.EndsWith(".msix", StringComparison.Ordinal) ? 512L * 1024 * 1024 : 262144;
                var digest = asset.GetProperty("digest").GetString() ?? "";
                if (id <= 0 || size <= 0 || size > limit || !DigestPattern().IsMatch(digest)) throw new InvalidDataException("Invalid release asset size or digest.");
                var file = Path.Combine(staging, name);
                await DownloadAsync(http, repository, token, id, file, size, cancellationToken);
                using var input = File.OpenRead(file);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken));
                if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("GitHub asset digest mismatch.");
            }
            store.ValidateAndPromote(staging, version, baseUri);
            return response.Headers.ETag;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
    private static async Task DownloadAsync(HttpClient http, string repository, string token, long id, string file, long size, CancellationToken cancellationToken)
    {
        using var request = ApiRequest($"https://api.github.com/repos/{repository}/releases/assets/{id}", token, binary: true);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Redirect)
        {
            var uri = response.Headers.Location;
            if (uri is not { IsAbsoluteUri: true } || uri.Scheme != "https" || uri.UserInfo.Length != 0 || !uri.IsDefaultPort ||
                uri.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("Unexpected GitHub asset redirect.");
            using var download = new HttpRequestMessage(HttpMethod.Get, uri);
            using var result = await http.SendAsync(download, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            result.EnsureSuccessStatusCode();
            await SaveAsync(result.Content, file, size, cancellationToken);
        }
        else
        {
            response.EnsureSuccessStatusCode();
            await SaveAsync(response.Content, file, size, cancellationToken);
        }
    }
    private static HttpRequestMessage ApiRequest(string url, string token, bool binary)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("VRCoplay-ReleaseSync/1.0");
        request.Headers.Accept.ParseAdd(binary ? "application/octet-stream" : "application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        return request;
    }
    private static async Task SaveAsync(HttpContent content, string file, long size, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        await CopyBoundedAsync(content, output, size, cancellationToken);
        if (output.Length != size) throw new InvalidDataException("Truncated GitHub release asset.");
    }
    private static async Task CopyBoundedAsync(HttpContent content, Stream output, long limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Release download exceeds its size limit.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[65536];
        long total = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            total += count;
            if (total > limit) throw new InvalidDataException("Release download exceeds its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9-]*/[A-Za-z0-9][A-Za-z0-9._-]*\z")]
    private static partial Regex RepositoryPattern();
    [GeneratedRegex(@"\Asha256:[a-fA-F0-9]{64}\z")]
    private static partial Regex DigestPattern();
}
