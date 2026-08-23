import { expect, test } from '@playwright/test';
import { Player, closeAll, createRoomWithRules, dealPlayable, fillWithBots } from './helpers';

/**
 * The bar under the claim buttons runs down as the window closes, so the pressure is visible
 * without reading the number.
 *
 * It used to divide the seconds left by a hardcoded 6, which is only the default window. A table
 * that gave players longer to think - the whole point of the setting - got a bar pinned full until
 * the last six seconds and then emptied all at once, which is worse than no bar: it says there is
 * plenty of time right up until there is none.
 *
 * Twenty seconds here rather than the six-second default, because the two behaviours are
 * indistinguishable at six.
 */

const WINDOW_SECONDS = 20;

/** Plays the host's turns against the bots until somebody's discard opens a claim window. */
async function playUntilClaim(player: Player, timeoutMs = 150_000): Promise<void> {
  const page = player.page;
  const claimBar = page.getByTestId('claim-bar');
  const turnBar = page.getByTestId('turn-bar');
  const outcome = page.getByTestId('outcome');

  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    await claimBar.or(turnBar).or(outcome).first().waitFor({ state: 'visible', timeout: 40_000 });

    if (await claimBar.isVisible()) return;
    if (await outcome.isVisible()) throw new Error('The hand finished before a claim window opened.');
    if (!(await turnBar.isVisible())) continue;

    // Never declare a win: the hand would end and there would be no more discards to claim.
    const last = page.getByTestId('my-hand').locator('.tile-button').last();
    if (!(await last.isEnabled().catch(() => false))) continue;

    await last.click({ timeout: 5_000 }).catch(() => undefined);
    await last.click({ timeout: 5_000 }).catch(() => undefined);
  }

  throw new Error(`No claim window opened within ${timeoutMs / 1000}s.`);
}

test('the countdown bar is scaled off the real window, not a hardcoded six', async ({ browser }) => {
  test.setTimeout(200_000);

  const { host } = await createRoomWithRules(browser, { claimWindowSeconds: WINDOW_SECONDS });
  await fillWithBots(host);
  await dealPlayable(host, [host]);

  const page = host.page;
  await playUntilClaim(host);

  const bar = page.getByTestId('claim-bar').locator('.timer');
  const countdown = page.getByTestId('claim-countdown');

  const scaleOf = async (): Promise<number> => {
    const transform = await bar.getAttribute('style');
    const found = /scaleX\(([\d.]+)\)/.exec(transform ?? '');
    if (!found) throw new Error(`No scaleX in the timer's style: ${transform}`);
    return Number(found[1]);
  };

  const secondsOf = async (): Promise<number> =>
    Number((await countdown.textContent())?.replace(/[^\d]/g, '') ?? '0');

  // The window opens near full, and the bar with it.
  expect(await secondsOf()).toBeGreaterThan(WINDOW_SECONDS - 5);
  expect(await scaleOf()).toBeGreaterThan(0.7);

  // Let it run down past halfway. This is the part the old code got wrong: 10 / 6 clamps to 1, so
  // the bar stayed completely full here.
  await expect
    .poll(secondsOf, { timeout: 30_000, intervals: [500] })
    .toBeLessThanOrEqual(WINDOW_SECONDS / 2);

  const seconds = await secondsOf();
  const scale = await scaleOf();

  expect(scale).toBeLessThan(0.7);

  // And it tracks the clock rather than merely being smaller: within a tick either way.
  expect(scale).toBeGreaterThan(seconds / WINDOW_SECONDS - 0.15);
  expect(scale).toBeLessThan(seconds / WINDOW_SECONDS + 0.15);

  await closeAll([host]);
});
