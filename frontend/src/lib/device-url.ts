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
