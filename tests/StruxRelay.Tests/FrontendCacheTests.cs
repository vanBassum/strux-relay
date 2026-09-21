using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StruxRelay.Cache;
using StruxRelay.Models;

namespace StruxRelay.Tests;

/// <summary>
/// What the cache may and may not keep. The interesting cases are all about a
/// fetch finishing AFTER something decided its device's files were no longer
/// valid — a reconnect, a Clear — because an entry stored then would live for the
/// whole of the next connection with nothing to evict it.
/// </summary>
public class FrontendCacheTests
{
    private const string Device = "esp32-test";

    private static FrontendCache NewCache(long maxBytes = 1024 * 1024) =>
        new(Options.Create(new CacheOptions { MaxBytes = maxBytes }),
            NullLogger<FrontendCache>.Instance);

    private static WebFile Ok(string body, string type = "text/html") =>
        new(new WebFileHeader(200, type, null), System.Text.Encoding.UTF8.GetBytes(body));

    /// <summary>A device whose answer is whatever the test hands it, when the test says so.</summary>
    private sealed class Source(string deviceId = Device) : IFrontendSource
    {
        private readonly TaskCompletionSource<WebFile> answer =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string DeviceId { get; } = deviceId;

        public int Reads { get; private set; }

        /// <summary>Completes once a read is actually in flight.</summary>
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Waits for this device to be read from, and gives up rather than hanging.
        /// A regression here does not return a wrong answer — it returns no answer,
        /// because the asker is waiting on a fetch that will never be started — and
        /// a test that hangs on CI says less than one that fails.
        /// </summary>
        public Task WaitForRead() => Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Answer(WebFile file) => answer.TrySetResult(file);

        public void Fail(Exception exception) => answer.TrySetException(exception);

        public async Task<WebFile> WebReadAsync(string path, CancellationToken cancellationToken)
        {
            Reads++;
            Started.TrySetResult();
            return await answer.Task;
        }
    }

    private static int FilesHeld(FrontendCache cache) =>
        cache.Snapshot().Devices.TryGetValue(Device, out var state) ? state.Files : 0;

    [Fact]
    public async Task A_fetch_that_finishes_after_a_drop_is_not_stored()
    {
        var cache = NewCache();
        var device = new Source();

        var get = cache.GetOrFetchAsync(device, "/index.html", CancellationToken.None);
        await device.WaitForRead();

        // The device reconnected (or somebody pressed Clear) while the read was in
        // flight. What comes back describes the connection that has just gone.
        cache.DropDevice(Device);
        device.Answer(Ok("<html>old</html>"));

        // The waiter still gets its bytes — it asked while that connection was live.
        var file = await get;
        Assert.Equal("<html>old</html>", System.Text.Encoding.UTF8.GetString(file.Body));

        // But nothing is kept.
        Assert.Equal(0, FilesHeld(cache));
        Assert.Equal(0, cache.Snapshot().Files);
    }

    [Fact]
    public async Task A_drop_detaches_an_inflight_fetch_so_the_next_asker_starts_a_fresh_one()
    {
        var cache = NewCache();
        var first = new Source();

        var stale = cache.GetOrFetchAsync(first, "/index.html", CancellationToken.None);
        await first.WaitForRead();

        cache.DropDevice(Device);

        // A page load arriving after the drop must not be handed the answer the
        // pre-drop fetch is about to produce.
        var second = new Source();
        var fresh = cache.GetOrFetchAsync(second, "/index.html", CancellationToken.None);
        await second.WaitForRead();

        first.Answer(Ok("<html>old</html>"));
        second.Answer(Ok("<html>new</html>"));

        Assert.Equal("<html>old</html>", System.Text.Encoding.UTF8.GetString((await stale).Body));
        Assert.Equal("<html>new</html>", System.Text.Encoding.UTF8.GetString((await fresh).Body));

        // And the one that is kept is the one from after the drop.
        var third = new Source();
        var served = await cache.GetOrFetchAsync(third, "/index.html", CancellationToken.None);
        Assert.Equal("<html>new</html>", System.Text.Encoding.UTF8.GetString(served.Body));
        Assert.Equal(0, third.Reads);
        Assert.Equal(1, FilesHeld(cache));
    }

    [Fact]
    public async Task Two_askers_share_one_fetch()
    {
        var cache = NewCache();
        var device = new Source();

        var a = cache.GetOrFetchAsync(device, "/index.html", CancellationToken.None);
        await device.WaitForRead();
        var b = cache.GetOrFetchAsync(device, "/index.html", CancellationToken.None);

        device.Answer(Ok("<html>one</html>"));
        await Task.WhenAll(a, b);

        Assert.Equal(1, device.Reads);
        Assert.Equal(1, FilesHeld(cache));
    }

    [Fact]
    public async Task A_failed_read_is_not_a_cache_entry()
    {
        var cache = NewCache();
        var device = new Source();

        var get = cache.GetOrFetchAsync(device, "/index.html", CancellationToken.None);
        await device.WaitForRead();

        // What a RESET, an over-long reply or a silent device all come out as: the
        // read throws rather than returning a short body, so there is nothing that
        // could be mistaken for a complete file.
        device.Fail(new InvalidOperationException("device went silent on web read"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => get);
        Assert.Equal(0, FilesHeld(cache));
        Assert.Equal(1, cache.Snapshot().FailedFetches);
    }

    [Fact]
    public async Task A_non_200_is_served_but_not_stored()
    {
        var cache = NewCache();
        var device = new Source();

        var get = cache.GetOrFetchAsync(device, "/missing.js", CancellationToken.None);
        await device.WaitForRead();
        device.Answer(new WebFile(new WebFileHeader(404, null, null), []));

        Assert.Equal(404, (await get).Header.Status);
        Assert.Equal(0, FilesHeld(cache));
    }

    [Fact]
    public async Task A_file_larger_than_the_whole_budget_is_served_but_not_stored()
    {
        var cache = NewCache(maxBytes: 64);
        var device = new Source();

        var get = cache.GetOrFetchAsync(device, "/assets/big-abcdefgh.js", CancellationToken.None);
        await device.WaitForRead();
        device.Answer(Ok(new string('x', 200), "application/javascript"));

        Assert.Equal(200, (await get).Body.Length);
        Assert.Equal(0, FilesHeld(cache));
    }

    [Fact]
    public async Task A_cached_file_is_served_without_asking_the_device_again()
    {
        var cache = NewCache();
        var first = new Source();

        var get = cache.GetOrFetchAsync(first, "/index.html", CancellationToken.None);
        await first.WaitForRead();
        first.Answer(Ok("<html>one</html>"));
        await get;

        var second = new Source();
        var again = await cache.GetOrFetchAsync(second, "/index.html", CancellationToken.None);

        Assert.Equal(0, second.Reads);
        Assert.Equal("<html>one</html>", System.Text.Encoding.UTF8.GetString(again.Body));
        Assert.Equal(1, cache.Snapshot().Hits);
    }

    [Theory]
    [InlineData("/assets/index-D8_Yz9Th.js", true)]
    [InlineData("/assets/index-Dn3dOpP2.css", true)]
    [InlineData("/index.html", false)]
    [InlineData("/assets/logo.svg", false)]
    public void Only_content_hashed_names_are_immutable(string path, bool immutable) =>
        Assert.Equal(immutable, FrontendCache.IsImmutable(path));
}
