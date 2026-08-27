import { Page, expect, test } from '@playwright/test';
import {
  Player,
  closeAll,
  createRoom,
  deal,
  drawIfOffered,
  fillWithBots,
  handSize,
  joinRoom,
  throwAnyTile,
} from './helpers';

/**
 * Every tile face shown inside an opponent's block of hidden tiles.
 *
 * The blocks are collapsed to a count by default, so each one is opened first. That is the state
 * worth checking anyway: what a player can see having deliberately asked to see it.
 */
async function opponentHiddenCodes(page: Page): Promise<string[]> {
  const eyes = page.locator('.opponent .eye');

  // Each one is waited on until it says it is open. Clicking and moving on used to be enough and
  // is not: the deal is still landing while this runs, and a click that arrives in the frame the
  // card is re-rendered in does nothing at all - leaving one opponent still collapsed and the
  // count sixteen short, which is a failure about this loop rather than about what is on screen.
  for (let i = 0; i < (await eyes.count()); i++) {
    const eye = eyes.nth(i);

    await expect(async () => {
      if ((await eye.getAttribute('aria-pressed')) !== 'true') await eye.click();
      await expect(eye).toHaveAttribute('aria-pressed', 'true', { timeout: 1_000 });
    }).toPass({ timeout: 10_000 });
  }

  return page
    .locator('.opponent .hidden-hand mj-tile .tile')
    .evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-code') ?? ''));
}

/** Throws whichever tile is currently at the end of the hand: lift, offer it up, confirm. */
async function throwLastTile(player: Player): Promise<void> {
  const tiles = player.page.getByTestId('my-hand').locator('.tile-button');
  const last = tiles.last();

  await last.click();
  await last.click();
  await player.page.getByTestId('discard-go').click();
}

test.describe('playing a hand', () => {
  test('the deal gives everyone sixteen tiles and the mano seventeen', async ({ browser }) => {
    const { host, code } = await createRoom(browser);
    const guests = [
      await joinRoom(browser, code, 'Tito Ben'),
      await joinRoom(browser, code, 'Ate Rose'),
      await joinRoom(browser, code, 'Kuya Jun'),
    ];
    const everyone = [host, ...guests];

    await deal(host, everyone);

    // Seat 0 deals first and holds the extra tile.
    expect(await handSize(host)).toBe(17);

    for (const guest of guests) {
      expect(await handSize(guest)).toBe(16);
    }

    await closeAll(everyone);
  });

  test('a player sees only their own tiles', async ({ browser }) => {
    const { host, code } = await createRoom(browser);
    const guests = [
      await joinRoom(browser, code, 'Tito Ben'),
      await joinRoom(browser, code, 'Ate Rose'),
      await joinRoom(browser, code, 'Kuya Jun'),
    ];
    const everyone = [host, ...guests];

    await deal(host, everyone);

    for (const player of everyone) {
      const hidden = await opponentHiddenCodes(player.page);
      const mine = await handSize(player);

      // Four hands hold 65 tiles between them at the deal (16 each plus the mano's extra), so
      // whatever this player is not holding must be showing as somebody else's face-down tile.
      // The count therefore differs for the mano, which is why it is derived rather than fixed.
      expect(hidden.length).toBe(65 - mine);

      // The assertion that actually matters: not one of them is face up.
      expect(new Set(hidden)).toEqual(new Set(['back']));
    }

    await closeAll(everyone);
  });

  test('throwing a tile passes the turn on', async ({ browser }) => {
    const { host, code } = await createRoom(browser);
    const guests = [
      await joinRoom(browser, code, 'Tito Ben'),
      await joinRoom(browser, code, 'Ate Rose'),
      await joinRoom(browser, code, 'Kuya Jun'),
    ];
    const everyone = [host, ...guests];

    await deal(host, everyone);

    await expect(host.page.getByTestId('turn-bar')).toBeVisible();
    await expect(host.page.getByTestId('discard-pool')).toContainText('No tiles thrown yet');

    await throwLastTile(host);

    // The thrown tile lands in the pool and the hand is back to sixteen.
    await expect(host.page.locator('.discards mj-tile')).toHaveCount(1);
    expect(await handSize(host)).toBe(16);

    // And everyone else can see it, because the pool is public.
    for (const guest of guests) {
      await expect(guest.page.locator('.discards mj-tile')).toHaveCount(1, { timeout: 10_000 });
    }

    await closeAll(everyone);
  });

  test('a player cannot throw a tile when it is not their turn', async ({ browser }) => {
    const { host, code } = await createRoom(browser);
    const guests = [
      await joinRoom(browser, code, 'Tito Ben'),
      await joinRoom(browser, code, 'Ate Rose'),
      await joinRoom(browser, code, 'Kuya Jun'),
    ];
    const everyone = [host, ...guests];

    await deal(host, everyone);

    // Seat 0 is to act. The others' tiles still take a tap - that is how a hand is grouped, and
    // waiting your turn is when anybody would want to tidy one - so what has to be true is that
    // nothing can leave their hand: two taps, the gesture that throws, and nothing moves.
    for (const guest of guests) {
      const tiles = guest.page.getByTestId('my-hand').locator('.tile-button');

      await tiles.first().click();
      await tiles.first().click();

      // Not even the question: the dialog is the discard step, and this seat is not on one.
      await expect(guest.page.getByTestId('discard-confirm')).toHaveCount(0);
      await expect(tiles).toHaveCount(16);
      await expect(guest.page.locator('.discards mj-tile')).toHaveCount(0);
    }

    await expect(host.page.getByTestId('my-hand').locator('.tile-button').first()).toBeEnabled();

    await closeAll(everyone);
  });

  test('reloading mid-hand puts the player back in the same seat with the same tiles', async ({ browser }) => {
    const { host, code } = await createRoom(browser);
    const guest = await joinRoom(browser, code, 'Tito Ben');
    const others = [await joinRoom(browser, code, 'Ate Rose'), await joinRoom(browser, code, 'Kuya Jun')];
    const everyone = [host, guest, ...others];

    await deal(host, everyone);

    const before = await guest.page
      .getByTestId('my-hand')
      .locator('.tile-button')
      .evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-tile-id')));

    expect(before).toHaveLength(16);

    await guest.page.reload();

    await expect(guest.page.getByTestId('table')).toBeVisible({ timeout: 20_000 });

    const after = await guest.page
      .getByTestId('my-hand')
      .locator('.tile-button')
      .evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-tile-id')));

    // Same physical tiles, not merely the same number of them.
    expect(after).toEqual(before);

    await closeAll(everyone);
  });

  test('a hand played out against bots reaches a scored ending', async ({ browser }) => {
    // Wide enough for the budget below to actually be spendable. A hand against three bots usually
    // takes about ninety seconds, and a slow one has been seen with four tiles left in the wall at
    // 150 - so the loop is given 210 and the test 300, where the old pairing of a 150s loop inside
    // a 180s test could time out while the table was still playing perfectly happily.
    test.setTimeout(300_000);

    const { host, code } = await createRoom(browser);
    await fillWithBots(host);
    await deal(host, [host]);

    const outcome = host.page.getByTestId('outcome');
    const turnBar = host.page.getByTestId('turn-bar');
    const claimBar = host.page.getByTestId('claim-bar');
    const drawBar = host.page.getByTestId('draw-bar');

    // Wait for whichever of the three actually happens next rather than polling on a counter.
    // Most of a hand is spent watching three bots play, so counting loop iterations would burn
    // the budget on waiting and stop the hand halfway through - which is exactly what it did.
    const somethingToDo = turnBar.or(claimBar).or(drawBar).or(outcome);

    const deadline = Date.now() + 210_000;

    while (Date.now() < deadline) {
      await somethingToDo.first().waitFor({ state: 'visible', timeout: 40_000 });

      if (await outcome.isVisible()) break;

      // Everything below is re-checked rather than assumed, because a claim window closes on the
      // server's clock. It can expire between the wait above and the click below, at which point
      // there is briefly nothing to do at all - and clicking a tile then would just hang on a
      // disabled button until the action timeout.
      if (await claimBar.isVisible()) {
        const todasClaim = host.page.getByTestId('claim-Todas');
        const button = (await todasClaim.isVisible()) ? todasClaim : host.page.getByTestId('claim-pass');

        await button.click({ timeout: 5_000 }).catch(() => undefined);
        continue;
      }

      // Nothing takes a tile off the wall by itself, so the turn only starts when this is pressed.
      if (await drawIfOffered(host)) continue;

      if (!(await turnBar.isVisible())) continue;

      // Declaring a win is always better than throwing another tile.
      const todas = host.page.getByTestId('declare-todas');
      if (await todas.isVisible()) {
        await todas.click();
        continue;
      }

      // The tolerant throw, not this file's own: the turn can end between the check above and the
      // taps below, and then no dialog opens because there is nothing left to ask about.
      await throwAnyTile(host);
    }

    // Short, because the loop above is what makes the hand move: if it ran out of budget without
    // an ending, waiting another two minutes on a table nobody is driving proves nothing.
    await expect(outcome).toBeVisible({ timeout: 30_000 });
    await expect(host.page.getByTestId('outcome-title')).not.toBeEmpty();

    await host.page.screenshot({ path: 'screenshots/hand-outcome.png', fullPage: true });

    // The result is a sheet over the table, not a door out of it. Closing it leaves the finished
    // table on screen to be looked at, and the action bar is what brings the result back.
    await host.page.getByTestId('outcome-close').click();
    await expect(outcome).toBeHidden();
    await expect(host.page.getByTestId('table')).toBeVisible();
    await host.page.screenshot({ path: 'screenshots/hand-over-table.png', fullPage: true });

    await host.page.getByTestId('show-outcome').click();
    await expect(outcome).toBeVisible();

    await host.page.getByTestId('back-to-lobby').click();
    await expect(host.page).toHaveURL(new RegExp(`/room/${code}$`));

    await closeAll([host]);
  });
});
