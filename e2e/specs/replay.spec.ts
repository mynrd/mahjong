import { Page, expect, test } from '@playwright/test';
import { PASSWORD, closeAll, createRoomWithRules, fillWithBots } from './helpers';

/**
 * The replay screen, driven the way somebody actually reaches it: open the link, type the room
 * password, pick a hand, step through it.
 *
 * A one-second claim window, because a hand has to finish before anything can be replayed and the
 * default six seconds spends most of a hand waiting for windows nobody wants to claim.
 */
const FAST = { claimWindowSeconds: 1 };

/** Plays hands until one finishes, then goes back to the lobby. */
async function playAHand(page: Page): Promise<void> {
  await page.getByTestId('start-hand').click();
  await expect(page).toHaveURL(/\/table$/, { timeout: 20_000 });

  // The three bots play themselves, and the host seat only has to keep throwing. The outcome sheet
  // is what says the hand is over.
  const outcome = page.getByTestId('outcome');
  const deadline = Date.now() + 260_000;

  while (!(await outcome.isVisible().catch(() => false)) && Date.now() < deadline) {
    // Passing is what keeps the hand moving. Every discard opens a window on this seat now, and the
    // ones the bots throw have no clock at all: they wait for the person to say they are done.
    const pass = page.getByTestId('claim-pass');
    if (await pass.isVisible().catch(() => false)) await pass.click().catch(() => {});

    // Nothing takes a tile off the wall by itself, so the turn starts here.
    const draw = page.getByTestId('draw');
    if (await draw.isEnabled().catch(() => false)) await draw.click().catch(() => {});

    if (await page.getByTestId('turn-bar').isVisible().catch(() => false)) {
      // Lift, offer it up, confirm. The second tap only asks now. Same gesture as play.spec.ts.
      const last = page.getByTestId('my-hand').locator('.tile-button').last();

      if (await last.isEnabled().catch(() => false)) {
        await last.click().catch(() => {});
        await last.click().catch(() => {});
        await page.getByTestId('discard-go').click().catch(() => {});
      }
    }

    await page.waitForTimeout(300);
  }

  await expect(outcome).toBeVisible({ timeout: 10_000 });
  await page.getByTestId('back-to-lobby').click();
  await expect(page.getByTestId('start-hand')).toBeEnabled({ timeout: 20_000 });
}

test.describe('replays', () => {
  test('the password gates the list, and a hand can be stepped through', async ({ browser }) => {
    // A whole hand has to be played before there is anything to replay, and bots deliberately take
    // 900ms a move so a human can follow them - plus a Next from this seat on every one of the
    // fifty-odd tiles they throw. That is several minutes before the replay screen is even opened,
    // so the default 90 seconds is nowhere near enough.
    test.setTimeout(420_000);

    const { host, code } = await createRoomWithRules(browser, FAST, { roomName: 'Replay Table' });
    await fillWithBots(host);
    await playAHand(host.page);

    // A browser that never sat down. Nothing about a seat helps here: the password is the gate.
    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto(`/room/${code}/replay`);
    await expect(page.getByTestId('replay-password')).toBeVisible();
    await expect(page.getByTestId('replay-list')).toHaveCount(0);

    await page.getByTestId('replay-password').fill('not-the-password');
    await page.getByTestId('replay-unlock').click();
    await expect(page.getByTestId('replay-error')).toContainText('password');
    await expect(page.getByTestId('replay-list')).toHaveCount(0);

    await page.getByTestId('replay-password').fill(PASSWORD);
    await page.getByTestId('replay-unlock').click();

    await expect(page.getByTestId('replay-list')).toBeVisible();
    await page.getByTestId('replay-hand-1').click();

    await expect(page).toHaveURL(new RegExp(`/room/${code}/replay/1$`));
    await expect(page.getByTestId('replay')).toBeVisible();

    // The whole point: all four seats face up, not just one.
    for (const seat of [0, 1, 2, 3]) {
      const tiles = page.getByTestId(`replay-seat-${seat}-hand`).locator('mj-tile');
      expect(await tiles.count()).toBeGreaterThanOrEqual(1);

      const faces = await tiles.locator('.tile[data-code]').evaluateAll((nodes) =>
        nodes.map((n) => n.getAttribute('data-code') ?? ''),
      );

      expect(faces.length).toBeGreaterThan(0);
      expect(faces).not.toContain('back');
    }

    // Stepping.
    const caption = page.getByTestId('replay-caption');
    const opening = await caption.textContent();

    await expect(page.getByTestId('replay-back')).toBeDisabled();
    await expect(page.getByTestId('replay-first')).toBeDisabled();

    for (let step = 0; step < 8; step++) await page.getByTestId('replay-forward').click();

    await expect(caption).not.toHaveText(opening!);
    await expect(caption).toContainText('9/');

    await page.getByTestId('replay-back').click();
    await expect(caption).toContainText('8/');

    await page.getByTestId('replay-first').click();
    await expect(caption).toContainText('1/');
    await expect(page.getByTestId('replay-back')).toBeDisabled();

    await page.getByTestId('replay-last').click();
    await expect(page.getByTestId('replay-forward')).toBeDisabled();
    await expect(page.getByTestId('replay-outcome')).toBeVisible();

    await context.close();
    await closeAll([host]);
  });

  test('the arrow keys step too, and the unlock survives a reload', async ({ browser }) => {
    test.setTimeout(420_000);

    const { host, code } = await createRoomWithRules(browser, FAST, { roomName: 'Replay Keys' });
    await fillWithBots(host);
    await playAHand(host.page);

    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto(`/room/${code}/replay`);
    await page.getByTestId('replay-password').fill(PASSWORD);
    await page.getByTestId('replay-unlock').click();
    await page.getByTestId('replay-hand-1').click();

    const caption = page.getByTestId('replay-caption');
    await expect(caption).toContainText('1/');

    await page.keyboard.press('ArrowRight');
    await page.keyboard.press('ArrowRight');
    await expect(caption).toContainText('3/');

    await page.keyboard.press('ArrowLeft');
    await expect(caption).toContainText('2/');

    // The token lives in sessionStorage, so a reload of the same tab does not ask again.
    await page.reload();
    await expect(page.getByTestId('replay')).toBeVisible();
    await expect(page.getByTestId('replay-password')).toHaveCount(0);

    await context.close();
    await closeAll([host]);
  });

  test('a table with no finished hands says so', async ({ browser }) => {
    const { host, code } = await createRoomWithRules(browser, FAST, { roomName: 'Nothing Played' });

    const context = await browser.newContext();
    const page = await context.newPage();

    await page.goto(`/room/${code}/replay`);
    await page.getByTestId('replay-password').fill(PASSWORD);
    await page.getByTestId('replay-unlock').click();

    await expect(page.getByTestId('replay-empty')).toBeVisible();

    await context.close();
    await closeAll([host]);
  });
});
