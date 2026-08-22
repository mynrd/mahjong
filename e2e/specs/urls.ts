/**
 * The API lives on the same host as the web app, on port 5080. Derived rather than configured, so
 * pointing the suite at a phone-reachable address (WEB_URL=http://192.168.254.100:4200) moves the
 * API with it and nothing else has to change.
 */
export function apiBaseUrlFor(webUrl: string): string {
  const url = new URL(webUrl);
  return `${url.protocol}//${url.hostname}:5080`;
}
