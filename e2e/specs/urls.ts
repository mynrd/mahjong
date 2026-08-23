/**
 * The API is served by the same server as the page, on the same port, so the API base is just the
 * app's own origin. Kept as a function so pointing the suite at a phone-reachable address
 * (WEB_URL=http://192.168.254.100:5080) moves the API with it and nothing else has to change.
 */
export function apiBaseUrlFor(webUrl: string): string {
  return new URL(webUrl).origin;
}
