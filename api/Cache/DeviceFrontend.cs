using Microsoft.AspNetCore.Http.Extensions;
using StruxRelay.Devices;

namespace StruxRelay.Cache;

/// <summary>
/// Serves a device's own frontend through the relay, from the cache when it can.
///
/// The device owns its frontend storage: the relay asks for <c>/index.html</c>
/// and never learns it lives gzipped on a FAT partition called <c>www</c>.
/// Content-Encoding comes back from the device, so gzip passes straight through
/// rather than being undone and redone here.
/// </summary>
internal static class DeviceFrontend
{
    public static async Task HandleAsync(
        HttpContext context,
        string deviceId,
        string path,
        DeviceRegistry registry,
        FrontendCache cache)
    {
        // /devices/<id> → /devices/<id>/, so the page's relative asset URLs
        // resolve against the device's own directory rather than the relay's root.
        //
        // Decided here rather than by a second route, and that is not a style
        // choice: ASP.NET matches "/devices/x" and "/devices/x/" with the same
        // template, so a separate redirect route caught the trailing-slash form
        // too and redirected it to itself — a loop. aiohttp told those apart;
        // this does not, so the raw path has to be read.
        if (path.Length == 0 && !context.Request.Path.Value!.EndsWith('/'))
        {
            context.Response.Redirect($"{context.Request.PathBase}{context.Request.Path}/");
            return;
        }

        var device = registry.Find(deviceId);
        if (device is null || !device.Online)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsync($"device '{deviceId}' is not connected");
            return;
        }

        var tail = path.TrimStart('/');
        var wanted = tail.Length == 0 ? "/index.html" : "/" + tail;

        CachedFile file;
        try
        {
            file = await cache.GetOrFetchAsync(device, wanted, context.RequestAborted);

            if (file.Header.Status != 200)
            {
                // SPA fallback is the RELAY's decision — the device answers a real
                // 404. Only paths that look like routes fall back: a mistyped asset
                // must stay a 404 rather than becoming HTML with status 200, which
                // a browser rejects as a MIME error.
                var leaf = tail.Split('/')[^1];
                if (leaf.Contains('.'))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsync($"{wanted} not found on {deviceId}");
                    return;
                }

                // Served under index.html's own key, so a fallback and a real
                // index.html request share one cache entry rather than each
                // holding a copy.
                wanted = "/index.html";
                file = await cache.GetOrFetchAsync(device, wanted, context.RequestAborted);
                if (file.Header.Status != 200)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsync("no index.html on the device");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The browser gave up. Nothing to say to it.
            return;
        }
        catch (Exception exception)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsync(exception.Message);
            return;
        }

        // Told to the BROWSER, which matters more than the server-side hit: an
        // immutable asset is not requested again at all, so a second page load
        // costs no pipe traffic rather than a cheap hit. Only content-hashed names
        // may be immutable; for everything else no-cache means "ask, but a 304 is
        // likely", which is one small conditional GET instead of a bundle.
        if (file.ETag is not null)
        {
            context.Response.Headers.ETag = file.ETag;
            context.Response.Headers.CacheControl = FrontendCache.IsImmutable(wanted)
                ? "public, max-age=31536000, immutable"
                : "no-cache";

            if (context.Request.Headers.IfNoneMatch.Contains(file.ETag))
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                return;
            }
        }

        if (!string.IsNullOrEmpty(file.Header.ContentEncoding))
            // Without this the browser gets gzip bytes labelled as JavaScript.
            context.Response.Headers.ContentEncoding = file.Header.ContentEncoding;

        context.Response.ContentType =
            string.IsNullOrEmpty(file.Header.ContentType)
                ? "application/octet-stream"
                : file.Header.ContentType;

        context.Response.ContentLength = file.Body.Length;
        await context.Response.Body.WriteAsync(file.Body, context.RequestAborted);
    }
}
