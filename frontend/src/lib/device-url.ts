/**
 * What to put in a device's `relay.url`. Derived from where this page was served
 * rather than configured, so it is right behind a reverse proxy without the relay
 * having to be told its own public name.
 *
 * In development that means the Vite port rather than Kestrel's — which still
 * works, because the dev server proxies /device through, but it is the dev URL and
 * not the one to hand a device that has to find the relay on its own.
 */
export function devicePipeUrl(): string {
  const { protocol, host } = window.location
  return `${protocol === "https:" ? "wss" : "ws"}://${host}/device`
}

/**
 * The device's own site, served by the relay over that device's pipe.
 *
 * This is the whole-page route: the device's `index.html` IS the page, and every
 * command and log line it shows afterwards rides `/devices/<id>/ws`. It is the
 * answer for a device that contributes no modules, which today is all of them and
 * in a mixed fleet will always be some of them — so this is a permanent path and
 * not scaffolding to delete once the shell can compose pages itself.
 *
 * The trailing slash is load-bearing: without it the page's relative asset URLs
 * resolve one level too high.
 */
export function deviceUiUrl(deviceId: string): string {
  return `/devices/${encodeURIComponent(deviceId)}/`
}
