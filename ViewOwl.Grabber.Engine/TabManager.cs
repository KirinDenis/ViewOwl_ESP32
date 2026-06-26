using System.Collections.Concurrent;
using PuppeteerSharp;

namespace ViewOwl.Grabber.Engine
{
    /// <summary>
    /// Thread-safe browser tab lifecycle manager.
    /// Maps each page's final URL to its open <see cref="IPage"/> instance.
    /// Navigation operations are serialised to prevent Chromium race conditions
    /// when opening multiple tabs concurrently.
    /// </summary>
    public sealed class TabManager : IDisposable
    {
        // Maps final page URL → open browser page.
        // ConcurrentDictionary allows lock-free reads from Find().
        private readonly ConcurrentDictionary<string, IPage> _tabs = new();

        // Serialise tab-open operations: viewport setup + style injection + navigation
        // must not race with each other inside Chromium.
        private readonly SemaphoreSlim _navLock = new(1, 1);

        // Hard ceiling on a single open-tab operation. A wedged browser process can make
        // NewPageAsync/GoToAsync hang indefinitely while holding _navLock, which freezes
        // every subsequent grab (observed: a 4-hour pipeline stall). Bounding the operation
        // guarantees OpenAsync always returns and releases the lock; a browser that keeps
        // timing out is then recycled by the GrabberWatchdogService.
        private const int NavigationTimeoutMs = 25_000;

        private IBrowser? _browser;

        // ── Initialisation ────────────────────────────────────────────────────

        /// <summary>
        /// Binds this manager to an open browser instance.
        /// Must be called exactly once, from <c>Chrome.InitAsync</c>, before any other method.
        /// </summary>
        /// <param name="browser">The running browser instance.</param>
        public void Initialize(IBrowser browser) => _browser = browser;

        private IBrowser Browser =>
            _browser ?? throw new InvalidOperationException(
                "TabManager is not initialised. Call Initialize(IBrowser) first.");

        // ── Tab management ────────────────────────────────────────────────────

        /// <summary>
        /// Opens a new browser tab, applies the given viewport and style injection,
        /// navigates to <paramref name="url"/>, and registers the page under its final URL.
        /// </summary>
        /// <param name="url">Navigation target (HTTP/HTTPS).</param>
        /// <param name="viewport">Display dimensions to set on the new page.</param>
        /// <param name="styleContent">CSS text to inject after the page loads.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>
        /// The new page, its final URL after any redirects, and a success flag.
        /// </returns>
        public async Task<(IPage? Page, string FinalUrl, bool Success)> OpenAsync(
            string url,
            ViewPortOptions viewport,
            string styleContent,
            CancellationToken ct = default)
        {
            await _navLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Task<(IPage? Page, string FinalUrl, bool Success)> work =
                    OpenCoreAsync(url, viewport, styleContent);

                // Backstop for a wedged browser where even GoToAsync's own timeout never fires
                // (the CDP connection is unresponsive). If the work does not finish in time we
                // abandon it and return failure so the lock is released and the worker proceeds.
                Task finished = await Task
                    .WhenAny(work, Task.Delay(NavigationTimeoutMs, ct))
                    .ConfigureAwait(false);

                if (finished != work)
                {
                    Console.WriteLine(
                        $"[Chrome] OpenAsync timed out after {NavigationTimeoutMs} ms: {url} " +
                        "— abandoning (watchdog will recycle the browser)");
                    return (null, url, false);
                }

                return await work.ConfigureAwait(false);
            }
            finally
            {
                _navLock.Release();
            }
        }

        /// <summary>
        /// Performs the actual tab-open work: new page, viewport, navigation, style injection,
        /// and registration. Bounded by the caller (<see cref="OpenAsync"/>) via a timeout.
        /// </summary>
        private async Task<(IPage? Page, string FinalUrl, bool Success)> OpenCoreAsync(
            string url, ViewPortOptions viewport, string styleContent)
        {
            IPage page = await Browser.NewPageAsync().ConfigureAwait(false);

            await page.SetViewportAsync(viewport).ConfigureAwait(false);

            // Navigate FIRST — injecting styles before GoToAsync would overwrite them
            // when the page document is replaced, and may throw on privileged blank pages.
            // An explicit Timeout makes a slow/never-loading page fail fast instead of
            // hanging on the default behaviour.
            var response = await page
                .GoToAsync(url, new NavigationOptions { Timeout = NavigationTimeoutMs })
                .ConfigureAwait(false);

            string finalUrl = page.Url;

            // Inject styles after the page has loaded so they survive in the live document
            if (!string.IsNullOrWhiteSpace(styleContent))
            {
                await page.AddStyleTagAsync(new AddTagOptions { Content = styleContent })
                          .ConfigureAwait(false);
            }

            // Register by final URL so Find() can locate it for screenshots
            _tabs.TryAdd(finalUrl, page);

            // response can be null for about:blank navigations; treat that as failure
            return (page, finalUrl, response?.Ok ?? false);
        }

        /// <summary>
        /// Returns the open page for the given URL, or <c>null</c> if not found.
        /// This call is lock-free.
        /// </summary>
        /// <param name="tabName">The page URL as returned by <see cref="OpenAsync"/>.</param>
        public IPage? Find(string tabName)
            => _tabs.TryGetValue(tabName, out IPage? page) ? page : null;

        /// <summary>
        /// Closes and removes the page identified by <paramref name="tabName"/>.
        /// Does nothing if the page is not registered.
        /// </summary>
        /// <param name="tabName">The page URL.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <summary>Releases the internal navigation semaphore.</summary>
        public void Dispose() => _navLock.Dispose();

        public async Task RemoveAsync(string tabName, CancellationToken ct = default)
        {
            if (_tabs.TryRemove(tabName, out IPage? page))
            {
                await page.CloseAsync().ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Clears all registered tab mappings and resets the browser reference.
        /// Called before re-initialising a crashed browser instance.
        /// Silently disposes any pages that can still be reached.
        /// </summary>
        public void Reset()
        {
            foreach (IPage page in _tabs.Values)
            {
                try { page.Dispose(); } catch { /* browser process may already be gone */ }
            }

            _tabs.Clear();
            _browser = null;
        }
    }
}
