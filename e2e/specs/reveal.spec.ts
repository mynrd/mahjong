import { expect, test } from '@playwright/test';
import { closeAll, createRoom, deal, fillWithBots, joinRoom, playUntilOutcome } from './helpers';

/**
 * Showing your hand after a hand is over, and looking at somebody else's.
 *
 * Two people at the table, not one and three bots: the whole point is what the second person can
 * see, and a bot has no screen to see it on. The hand itself is only a way of getting to the end
 * of a hand - what is being tested starts once the result is up.
 */
test.describe('showing a hand once it is over', () => {
  test('a hand is shown to the table only when its owner says so', async ({ browser }) => {
    test.setTimeout(240_000);

    const { host, code } = await createRoom(browser);
    const guest = await joinRoom(browser, code, 'Guest');

    await fillWithBots(host);
    await deal(host, [host, guest]);

    const players = [host, guest];

    await playUntilOutcome(players);

    // ---------------------------------------------------------------- before anybody shows

    await guest.page.getByTestId('outcome-close').click();

    const hostCard = guest.page.getByTestId('opponent-0-hand');
    await expect(hostCard).toBeHidden();
    await expect(guest.page.getByTestId('opponent-0-count')).toBeVisible();

    // The point of view: the guest opens the host's seat and finds only what was on the table.
    await guest.page.getByTestId('opponent-0-open').click();
    await expect(guest.page.getByTestId('seat-sheet')).toBeVisible();
    await expect(guest.page.getByTestId('seat-sheet-hand')).toBeHidden();
    await guest.page.getByTestId('seat-sheet-close').click();

    // ---------------------------------------------------------------- the host shows

    await host.page.getByTestId('outcome-reveal').click();
    await expect(host.page.getByTestId('outcome-revealed')).toBeVisible();

    await expect(hostCard).toBeVisible({ timeout: 15_000 });

    // Waited for rather than read straight away. The tiles arrive over the hub, and a read taken
    // the instant the container appears can land on it while it is still empty.
    await expect(hostCard.locator('mj-tile').first()).toBeVisible();

    const mine = await host.page
      .getByTestId('my-hand')
      .locator('mj-tile .tile[data-code]')
      .evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-code') ?? '').sort());

    const asSeenByGuest = await hostCard
      .locator('mj-tile .tile[data-code]')
      .evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-code') ?? '').sort());

    expect(asSeenByGuest.length).toBeGreaterThan(0);
    expect(asSeenByGuest).toEqual(mine);

    // The same tiles again in the seat sheet, which is where they are big enough to read.
    await guest.page.getByTestId('opponent-0-open').click();

    const sheetHand = guest.page.getByTestId('seat-sheet-hand');
    await expect(sheetHand.locator('mj-tile')).toHaveCount(mine.length);

    const inSheet = await sheetHand
      .locator('mj-tile .tile[data-code]')
      .evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-code') ?? '').sort());

    expect(inSheet).toEqual(mine);
    await guest.page.screenshot({ path: 'screenshots/revealed-hand.png', fullPage: true });
    await guest.page.getByTestId('seat-sheet-close').click();

    // ---------------------------------------------------------------- and nobody else

    // One seat showing says nothing about the rest. The bots never asked to show anything, and the
    // guest's own hand is the guest's business.
    for (const seat of [2, 3]) {
      await expect(guest.page.getByTestId(`opponent-${seat}-hand`)).toBeHidden();
      await expect(guest.page.getByTestId(`opponent-${seat}-count`)).toBeVisible();
    }

    await expect(host.page.getByTestId('opponent-1-hand')).toBeHidden();

    await closeAll(players);
  });
});
