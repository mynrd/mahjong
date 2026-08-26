import { expect, test } from '@playwright/test';
import { Player, closeAll, createRoomWithRules, deal, handSize, joinRoom } from './helpers';

/**
 * Four people and no bots.
 *
 * Everywhere else in this suite the table is driven against bots, which means waiting for a game
 * clock and asserting whatever the deal happened to allow. Nothing here needs luck: a discard goes
 * down, three seats answer it, and the seat after the thrower takes a tile. So this is where the
 * things that must hold on *every* discard live - the window opening on all three seats whatever
 * they are holding, and the fact that nothing at all happens until they answer.
 *
 * A long claim window, because a person's discard is on a clock and six seconds is not enough to
 * drive four browsers through one.
 */
const TABLE = { claimWindowSeconds: 120, jokerEnabled: false };

interface Seated {
  host: Player;
  guests: Player[];
  everyone: Player[];
}

async function sitFour(browser: Parameters<typeof createRoomWithRules>[0]): Promise<Seated> {
  const { host, code } = await createRoomWithRules(browser, TABLE);

  const guests = [
    await joinRoom(browser, code, 'Tito Ben'),
    await joinRoom(browser, code, 'Ate Rose'),
    await joinRoom(browser, code, 'Kuya Jun'),
  ];

  const everyone = [host, ...guests];
  await deal(host, everyone);

  return { host, guests, everyone };
}

/** Throws whichever tile is at the end of the hand: lift, offer it up, confirm. */
async function throwLastTile(player: Player): Promise<void> {
  const last = player.page.getByTestId('my-hand').locator('.tile-button').last();

  await last.click();
  await last.click();
  await player.page.getByTestId('discard-go').click();
}

test.describe('the table controls', () => {
  test('every discard is put in front of every other seat, and waits there', async ({ browser }) => {
    test.setTimeout(120_000);

    const { host, guests, everyone } = await sitFour(browser);

    try {
      await throwLastTile(host);

      // All three, whatever they are holding. Which of them can use it is the one thing the table
      // must never give away by what it draws - the window used to open only when somebody could
      // take the tile, so its appearing was the answer.
      for (const guest of guests) {
        await expect(guest.page.getByTestId('claim-bar')).toBeVisible({ timeout: 20_000 });
        await expect(guest.page.getByTestId('claim-bar')).toContainText(`${host.name} threw`);
      }

      // The thrower is not asked anything, and is told what the hold-up is.
      await expect(host.page.getByTestId('claim-bar')).toHaveCount(0);
      await expect(host.page.getByTestId('claim-waiting')).toBeVisible();

      // Two of the three answering is not enough: the tile is still on the table.
      await guests[1].page.getByTestId('claim-pass').click();
      await guests[2].page.getByTestId('claim-pass').click();

      await expect(guests[0].page.getByTestId('claim-bar')).toBeVisible();
      await expect(host.page.getByTestId('claim-waiting')).toBeVisible();

      // The third one lets it go, and the seat after the thrower is up.
      await guests[0].page.getByTestId('claim-pass').click();

      await expect(guests[0].page.getByTestId('draw-bar')).toBeVisible({ timeout: 20_000 });
    } finally {
      await closeAll(everyone);
    }
  });

  test('a tile only leaves the wall when Draw is pressed', async ({ browser }) => {
    test.setTimeout(120_000);

    const { host, guests, everyone } = await sitFour(browser);
    const next = guests[0];

    try {
      // The button is on the bar from the first frame, dead. The mano is holding seventeen tiles
      // already: its turn started with the deal, so there is nothing to take.
      await expect(host.page.getByTestId('draw')).toBeVisible();
      await expect(host.page.getByTestId('draw')).toBeDisabled();

      await throwLastTile(host);

      // Not while the window is open, not even for the seat about to play. A person threw this one
      // at a table with the helper on, so it is on the house clock and will end by itself; drawing
      // through is only offered where nothing else would ever close the window.
      await expect(next.page.getByTestId('claim-bar')).toBeVisible({ timeout: 20_000 });
      await expect(next.page.getByTestId('draw')).toBeDisabled();
      await expect(guests[1].page.getByTestId('draw')).toBeDisabled();

      for (const guest of guests) await guest.page.getByTestId('claim-pass').click();

      // Now it is squarely this seat's turn, and the hand is still sixteen tiles: nothing was
      // handed to it. That is the whole change - the tile used to arrive on its own, while the
      // player was still looking at what had just been thrown.
      await expect(next.page.getByTestId('draw-bar')).toBeVisible({ timeout: 20_000 });
      expect(await handSize(next)).toBe(16);

      await next.page.getByTestId('draw').click();

      await expect(next.page.getByTestId('turn-bar')).toBeVisible({ timeout: 20_000 });
      expect(await handSize(next)).toBe(17);
      await expect(next.page.getByTestId('draw')).toBeDisabled();
    } finally {
      await closeAll(everyone);
    }
  });

  test('Sort puts the hand back to one plain block', async ({ browser }) => {
    test.setTimeout(120_000);

    const { host, everyone } = await sitFour(browser);
    const page = host.page;

    try {
      const blocks = page.getByTestId('my-hand').locator('.group');

      // Group two tiles by hand: tap one, tap another. That leaves a block of two and a block of
      // everything still loose.
      const tiles = page.getByTestId('my-hand').locator('.tile-button');
      const first = await tiles.first().getAttribute('data-tile-id');
      const second = await tiles.nth(1).getAttribute('data-tile-id');

      // Not on this seat's discard step, where a tap means throw. The grouping gesture is only
      // live while there is nothing else a tap could mean, so the tile goes down first.
      await throwLastTile(host);
      await expect(page.getByTestId('claim-waiting')).toBeVisible({ timeout: 20_000 });

      await page.locator(`.tile-button[data-tile-id="${first}"]`).click();
      await page.locator(`.tile-button[data-tile-id="${second}"]`).click();

      await expect(blocks).toHaveCount(2);

      await page.getByTestId('sort-hand').click();

      // One block, every tile in it, nothing grouped.
      await expect(blocks).toHaveCount(1);
      await expect(page.getByTestId('my-hand').locator('.group[data-group="all"]')).toHaveCount(1);
      expect(await handSize(host)).toBe(16);
    } finally {
      await closeAll(everyone);
    }
  });

  test('a long press on a tile never raises the browser menu', async ({ browser }) => {
    test.setTimeout(120_000);

    const { host, everyone } = await sitFour(browser);
    const page = host.page;

    try {
      // Every tile is an image, and the menu behind one offers to preview it, copy it or save it -
      // over the top of the hand, mid-turn. There is nothing on this page worth taking away, so
      // the event is refused rather than being left to each element to defend itself.
      const defaultPrevented = await page
        .getByTestId('my-hand')
        .locator('.tile-button')
        .first()
        .evaluate((node) => {
          const event = new MouseEvent('contextmenu', { bubbles: true, cancelable: true });
          node.dispatchEvent(event);
          return event.defaultPrevented;
        });

      expect(defaultPrevented).toBe(true);
    } finally {
      await closeAll(everyone);
    }
  });

  test('the second tap asks before the tile leaves the hand', async ({ browser }) => {
    test.setTimeout(120_000);

    const { host, guests, everyone } = await sitFour(browser);
    const page = host.page;

    try {
      const tiles = page.getByTestId('my-hand').locator('.tile-button');
      const id = await tiles.last().getAttribute('data-tile-id');
      const target = page.getByTestId('my-hand').locator(`.tile-button[data-tile-id="${id}"]`);

      // Two taps used to be the throw itself. Now they are the question.
      await target.click();
      await target.click();

      await expect(page.getByTestId('discard-confirm')).toBeVisible();
      await page.getByTestId('discard-cancel').click();

      // Nothing was sent: the tile is still in the hand, and no other seat has been shown one.
      await expect(page.getByTestId('discard-confirm')).toHaveCount(0);
      expect(await handSize(host)).toBe(17);
      await expect(page.locator('.discards mj-tile')).toHaveCount(0);
      await expect(guests[0].page.getByTestId('claim-bar')).toHaveCount(0);

      // And the same two taps followed by the other answer does throw it.
      await target.click();
      await target.click();
      await page.getByTestId('discard-go').click();

      await expect(page.locator('.discards mj-tile')).toHaveCount(1);
      expect(await handSize(host)).toBe(16);
    } finally {
      await closeAll(everyone);
    }
  });
});
