import { apiBaseUrlFor } from './specs/urls';

/**
 * Runs once before the suite.
 *
 * Two jobs. First, fail immediately and legibly if the servers are not running, rather than
 * letting every spec time out one by one against a dead port. Second, warm the API: its first
 * request builds the EF model, checks migrations and opens the first SQL connection, which took
 * long enough to make whichever spec happened to run first fail on its own.
 */
export default async function globalSetup(): Promise<void> {
  const webUrl = process.env.WEB_URL ?? 'http://localhost:4200';
  const apiUrl = apiBaseUrlFor(webUrl);

  await waitFor('API', `${apiUrl}/api/health`, 60_000);
  await waitFor('web app', webUrl, 60_000);

  // A couple of real round trips so the model and the connection pool are already up.
  for (let i = 0; i < 3; i++) await fetch(`${apiUrl}/api/health`).catch(() => undefined);
}

async function waitFor(what: string, url: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastError = '';

  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) return;
      lastError = `HTTP ${response.status}`;
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error);
    }

    await new Promise((resolve) => setTimeout(resolve, 500));
  }

  throw new Error(
    `The ${what} did not answer at ${url} within ${timeoutMs / 1000}s (${lastError}).\n` +
      `Start both servers first, either with run.ps1 from the repo root or:\n` +
      `  server:  dotnet run --project src/Mahjong.Api --urls http://0.0.0.0:5080\n` +
      `  web:     npx ng serve --host 0.0.0.0 --port 4200 --allowed-hosts`,
  );
}
