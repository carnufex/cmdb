using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cmdb.Catalog;
using FastEndpoints;

namespace Cmdb.Api.Features.Catalog;

public sealed record CatalogImageRequest(string File);

/// <summary>
/// A panel image from the catalog (#214), embedded or from <c>CMDB_CATALOG_PATH</c>. Behind sign-in like the rest of the
/// catalog. An SVG may carry script, so it is served with a policy that runs none, should anyone open it on its own.
/// </summary>
public sealed class GetCatalogImageEndpoint(CatalogImages images) : Endpoint<CatalogImageRequest>
{
    public override void Configure() => Get("/catalog/images/{file}");

    public override async Task HandleAsync(CatalogImageRequest req, CancellationToken ct)
    {
        if (images.Find(req.File) is not var (bytes, etag))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var headers = HttpContext.Response.Headers;
        headers.ETag = etag;
        headers.CacheControl = "private, no-cache";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
        if (HttpContext.Request.Headers.IfNoneMatch.Contains(etag))
        {
            await Send.StatusCodeAsync(StatusCodes.Status304NotModified, ct);
            return;
        }
        var contentType = req.File.EndsWith(".svg", StringComparison.Ordinal) ? "image/svg+xml" : "image/png";
        await Send.BytesAsync(bytes, contentType: contentType, cancellation: ct);
    }
}

/// <summary>The catalog's panel images, read and hashed once each: the catalog is fixed for the life of the process.</summary>
public sealed class CatalogImages(CatalogSource source)
{
    // Only files that exist are kept, so made-up names cannot grow the cache.
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string ETag)> _cache = new(StringComparer.Ordinal);

    public (byte[] Bytes, string ETag)? Find(string file)
    {
        if (_cache.TryGetValue(file, out var hit))
        {
            return hit;
        }
        if (source.Image(file) is not { } bytes)
        {
            return null;
        }
        return _cache[file] = (bytes, $"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))[..16]}\"");
    }
}
