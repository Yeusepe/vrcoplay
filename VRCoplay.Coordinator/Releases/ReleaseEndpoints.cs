// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
public interface IReleaseAccessPolicy
{
    ValueTask<bool> CanDownloadAsync(HttpContext context);
}
public sealed class PublicReleaseAccess : IReleaseAccessPolicy
{
    public ValueTask<bool> CanDownloadAsync(HttpContext context) => ValueTask.FromResult(true);
}
public static partial class ReleaseEndpoints
{
    public static void Map(WebApplication app)
    {
        var configured = app.Configuration["RELEASE_DIRECTORY"];
        if (string.IsNullOrWhiteSpace(configured)) return;
        var directory = Path.GetFullPath(configured);
        if (!Path.IsPathFullyQualified(configured)) throw new InvalidOperationException("RELEASE_DIRECTORY must be absolute.");
        Directory.CreateDirectory(directory);
        var store = new ReleaseStore(directory);
        var page = new ReleasePage(store);
        foreach (var path in new[] { "/alpha", "/releases" })
        {
            var releases = app.MapGroup(path);
            releases.AddEndpointFilter(async (invocation, next) =>
            {
                var context = invocation.HttpContext;
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers.CacheControl = "private, no-cache";
                var policy = context.RequestServices.GetRequiredService<IReleaseAccessPolicy>();
                return await policy.CanDownloadAsync(context) ? await next(invocation) : Results.Unauthorized();
            });
            releases.MapGet("/", () => Results.Redirect("/alpha/index.html"));
            releases.MapMethods("/{name}", ["GET", "HEAD"], (string name) =>
            {
                if (!SafeName().IsMatch(name) || !ReleaseStore.IsPublicFile(name)) return Results.NotFound();
                var type = Path.GetExtension(name).ToLowerInvariant() switch
                {
                    ".msix" => "application/msix",
                    ".appinstaller" => "application/appinstaller",
                    ".cer" => "application/pkix-cert",
                    ".unitypackage" => "application/octet-stream",
                    ".json" => "application/json; charset=utf-8",
                    ".txt" => "text/plain; charset=utf-8",
                    ".html" when name == "index.html" => "text/html; charset=utf-8",
                    _ => null
                };
                if (type is null) return Results.NotFound();
                if (name is "index.html" or "SHA256SUMS.txt")
                {
                    try
                    {
                        var content = page.Get(name);
                        return content is null ? Results.NotFound() : Results.Bytes(content, type);
                    }
                    catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        app.Logger.LogWarning("Download page metadata is unavailable ({FailureType}).", error.GetType().Name);
                        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        var refreshToken = app.Configuration["RELEASE_REFRESH_TOKEN"];
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            app.MapPost("/releases/refresh", (HttpContext context) =>
            {
                var provided = context.Request.Headers.Authorization.ToString();
                var expected = "Bearer " + refreshToken;
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected)))
                    return Results.NotFound();
                context.RequestServices.GetRequiredService<ReleaseSyncTrigger>().Trigger();
                return Results.Accepted();
            });
        }
    }
                var file = store.Resolve(name);
                if (file is null) return Results.NotFound();
                return Results.File(file, type, fileDownloadName: name.EndsWith(".appinstaller", StringComparison.OrdinalIgnoreCase) || name == ReleaseStore.UnityPackageName ? name : null,
                    enableRangeProcessing: true, lastModified: File.GetLastWriteTimeUtc(file));
            });
        }
    }
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,150}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();
}
