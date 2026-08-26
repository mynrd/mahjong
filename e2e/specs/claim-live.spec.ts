import { expect, test } from '@playwright/test';
import {
  Player,
  closeAll,
  createRoomWithRules,
  dealPlayable,
  drawIfOffered,
  fillWithBots,
  throwAnyTile,
} from './helpers';

/**
 * A thrown tile stays yours to answer until somebody takes it or the next seat picks up. Nothing
 * counts it down, and closing the dialog is not an answer.
 *
 * This replaces a spec that measured the claim window emptying a countdown bar. That window is
 * gone: it was six seconds from the throw, and a player who looked away for a moment - or shut the
 * dialog to see their own hand - was out of the discard with nothing on screen to say why, and
 * nothing but Draw left to press. What is worth driving a browser for now is the way back in.
 *
 * The bot in the seat after the thrower is given a long patience, because that draw is the one
 * thing that does still end a window, and it would otherwise end this one mid-test.
 */
const PATIENT = { jokerEnabled: false, botPatienceSeconds: 600 };

/** Plays the host's turns against the bots until a claim window opens on it. */
async function playUntilClaim(host: Player, timeoutMs = 150_000): Promise<void> {
  const dialog = host.page.getByTestId('claim-bar');
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    if (await dialog.isVisible().catch(() => false)) return;

    await drawIfOffered(host);

    if (await host.page.getByTestId('turn-bar').isVisible().catch(() => false)) {
      await throwAnyTile(host);
    }

    await dialog.waitFor({ state: 'visible', timeout: 1_500 }).catch(() => undefined);
  }

  throw new Error(`No claim window opened within ${timeoutMs / 1000}s.`);
}

test('a dismissed claim dialog can be opened again off the tile in the pool', async ({
  browser,
}) => {
  test.setTimeout(200_000);

  const { host } = await createRoomWithRules(browser, PATIENT);
  const players = [host];

  try {
    await fillWithBots(host);
    await dealPlayable(host, [host]);
    await playUntilClaim(host);

    const page = host.page;
    const dialog = page.getByTestId('claim-bar');
    const live = page.locator('.discards mj-tile[data-claimable="yes"]');

    // Nothing is counting, so the dialog is not a thing to beat.
    await expect(page.getByTestId('claim-countdown')).toHaveText('no rush');

    // Exactly one tile in the pool is marked as still answerable, and it is the last one thrown.
    await expect(live).toHaveCount(1);

    await page.getByTestId('claim-close').click();
    await expect(dialog).toBeHidden();

    // Well past the six seconds the old window gave, and the tile is still marked and still there
    // to press. This is the whole of the change: shutting the dialog cost nothing.
    await page.waitForTimeout(9_000);
    await expect(live).toHaveCount(1);
    await expect(page.getByTestId('claim-strip')).toBeVisible();

    // And pressing it is the way back to the calls, without going near the bar.
    await live.click();
    await expect(dialog).toBeVisible();

    // Passing is still the only thing that takes the tile off you, and it takes the mark with it.
    await page.getByTestId('claim-pass').click();
    await expect(live).toHaveCount(0, { timeout: 20_000 });
  } finally {
    await closeAll(players);
  }
});
