// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
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
