namespace StruxRelay.Cache;

internal sealed class CacheOptions
{
    public const string Section = "Relay:Cache";

    /// <summary>
    /// Bytes, not entries. A device's frontend is one small index.html and one
    /// large bundle, and it is the bundle that decides whether this fits in a
    /// container — counting entries would say nothing useful about that.
    /// </summary>
    public long MaxBytes { get; set; } = 32 * 1024 * 1024;

    /// <summary>
    /// Pull a device's frontend as soon as it connects, rather than during
    /// somebody's first page load. Off makes every first view pay N sequential
    /// round trips over the pipe.
    /// </summary>
    public bool WarmOnConnect { get; set; } = true;
}
