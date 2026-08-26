import { Locator, Page, expect, test } from '@playwright/test';
import { closeAll, createRoom, dealPlayable, fillWithBots, handSize, joinRoom } from './helpers';

/**
 * The hand you are holding, on the screen it is hardest to fit on.
 *
 * Two rules this pins. Your own hand never scrolls sideways - it wraps and scrolls down, because a
 * sideways scrollbar hides two thirds of your tiles behind a gesture nobody knows is there. And
 * lifting the box shows every tile at once with no scrolling at all, which is the whole point of
 * lifting it.
 */

/**
 * How far the tiles stick out past the sides of the hand, in pixels, and how far past the bottom.
 *
 * Measured off the tiles rather than off scrollWidth: the hand is `overflow-x: hidden`, which
 * pins scrollWidth to clientWidth whatever the contents are doing, so a scrollWidth check here
 * passes even when two thirds of the hand is clipped off the side.
 */
async function spill(page: Page): Promise<{ sideways: number; down: number }> {
  return page.getByTestId('my-hand').evaluate((hand) => {
    const box = hand.getBoundingClientRect();
    let sideways = 0;

    for (const tile of hand.querySelectorAll('.tile-button')) {
      const rect = tile.getBoundingClientRect();
      sideways = Math.max(sideways, box.left - rect.left, rect.right - box.right);
    }

    return { sideways, down: hand.scrollHeight - hand.clientHeight };
  });
}

/**
 * Drags one tile into the gap on one side of another, which is what puts them in the same group.
 * The side is the half of the target the drop lands on, so `before` aims a quarter of the way in
 * from its leading edge and `after` a quarter in from the trailing one.
 */
async function dragTile(
  page: Page,
  fromId: string,
  ontoId: string,
  side: 'before' | 'after' = 'after',
): Promise<void> {
  const hand = page.getByTestId('my-hand');
  const from = (await hand.locator(`.tile-button[data-tile-id="${fromId}"]`).boundingBox())!;
  const onto = (await hand.locator(`.tile-button[data-tile-id="${ontoId}"]`).boundingBox())!;
  const x = onto.x + onto.width * (side === 'before' ? 0.25 : 0.75);

  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();

  // Two moves: the first arms the drag past the threshold, the second lands it on the target.
  await page.mouse.move(from.x + from.width / 2, from.y - 24, { steps: 4 });
  await page.mouse.move(x, onto.y + onto.height / 2, { steps: 10 });
  await page.mouse.up();
}

/** The tiles of a block, left to right, as ids - the order the group is actually held in. */
function blockIds(block: Locator): Promise<(string | null)[]> {
  return block.locator('.tile-button').evaluateAll((nodes) =>
    nodes.map((n) => n.getAttribute('data-tile-id')),
  );
}

test.describe('your own hand', () => {
  test('never scrolls sideways, and shows every tile once lifted', async ({ browser }) => {
    const { host } = await createRoom(browser);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    const page = host.page;
    const lift = page.getByTestId('toggle-hand');

    // Down. Seventeen tiles do not fit on a phone, so some of them are below the fold - but never
    // off to the side. One pixel of slack for sub-pixel layout rounding.
    await expect(lift).toHaveAttribute('aria-expanded', 'false');

    expect((await spill(page)).sideways).toBeLessThanOrEqual(1);

    // Up. Every tile is on screen: nothing left to scroll in either direction.
    await lift.click();
    await expect(lift).toHaveAttribute('aria-expanded', 'true');

    const up = await spill(page);
    expect(up.sideways).toBeLessThanOrEqual(1);
    expect(up.down).toBeLessThanOrEqual(1);

    // And the tiles really are all there, not clipped out of the DOM.
    expect(await handSize(host)).toBe(17);

    for (const tile of await page.getByTestId('my-hand').locator('.tile-button').all()) {
      await expect(tile).toBeInViewport();
    }

    // The page itself must not have grown past the viewport to make room.
    const overflow = await page.evaluate(
      () => document.documentElement.scrollHeight - window.innerHeight,
    );
    expect(overflow).toBeLessThanOrEqual(1);

    await page.screenshot({ path: 'screenshots/hand-lifted.png' });

    await closeAll([host]);
  });

  test('the lift survives a reload', async ({ browser }) => {
    const { host } = await createRoom(browser);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    await host.page.getByTestId('toggle-hand').click();
    await expect(host.page.getByTestId('toggle-hand')).toHaveAttribute('aria-expanded', 'true');

    await host.page.reload();
    await expect(host.page.getByTestId('table')).toBeVisible({ timeout: 20_000 });

    await expect(host.page.getByTestId('toggle-hand')).toHaveAttribute('aria-expanded', 'true');

    await closeAll([host]);
  });

  test('grouping survives a reload, because the server is holding it', async ({ browser }) => {
    const { host } = await createRoom(browser);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    const page = host.page;
    const tiles = page.getByTestId('my-hand').locator('.tile-button');

    // There is no mode to enter any more: with Auto Arrange off, dragging one tile onto another
    // groups them whenever you like, hand up or down, your turn or not.
    await expect(page.getByTestId('auto-arrange')).toHaveAttribute('aria-pressed', 'false');
    await page.getByTestId('toggle-hand').click();

    const first = await tiles.nth(0).getAttribute('data-tile-id');
    const second = await tiles.nth(1).getAttribute('data-tile-id');

    await dragTile(page, first!, second!);

    const group = page.getByTestId('my-hand').locator('.group[data-group="manual"]');
    await expect(group).toHaveCount(1);
    await expect(group.locator('.tile-button')).toHaveCount(2);

    // Still seventeen tiles: grouping moves tiles, it never invents or loses one.
    expect(await handSize(host)).toBe(17);

    // The debounced save has to land before the reload throws the page away.
    await page.waitForTimeout(1200);
    await page.reload();
    await expect(page.getByTestId('table')).toBeVisible({ timeout: 20_000 });

    const restored = page.getByTestId('my-hand').locator('.group[data-group="manual"]');
    await expect(restored).toHaveCount(1);

    const ids = await restored
      .locator('.tile-button')
      .evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-tile-id')));

    expect(ids.slice().sort()).toEqual([first, second].sort());
    expect(await handSize(host)).toBe(17);

    await closeAll([host]);
  });

  test('a tile lands in the gap it was dropped in, not on the end of the group', async ({
    browser,
  }) => {
    const { host } = await createRoom(browser);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    const page = host.page;
    const hand = page.getByTestId('my-hand');
    const tiles = hand.locator('.tile-button');

    await page.getByTestId('toggle-hand').click();

    // Read up front and held as ids: grouping moves tiles around the hand, so a position is only
    // good until the next drop while an id is the same tile all the way through.
    const [first, second, third, fourth] = (await blockIds(hand)).slice(0, 4);

    // A run of three, built left to right.
    await dragTile(page, second!, first!);
    await dragTile(page, third!, second!);

    const group = hand.locator('.group[data-group="manual"]');
    await expect(group.locator('.tile-button')).toHaveCount(3);
    await expect.poll(() => blockIds(group)).toEqual([first, second, third]);

    // The point of the whole thing: dropped on the leading half of the second tile, the fourth
    // goes between the first two rather than onto the end.
    await dragTile(page, fourth!, second!, 'before');
    await expect.poll(() => blockIds(group)).toEqual([first, fourth, second, third]);

    // And the front of the group is reachable the same way - there is a gap before the first tile.
    await dragTile(page, fourth!, first!, 'before');
    await expect.poll(() => blockIds(group)).toEqual([fourth, first, second, third]);

    // The trailing half of the last tile is the other end of it.
    await dragTile(page, fourth!, third!, 'after');
    await expect.poll(() => blockIds(group)).toEqual([first, second, third, fourth]);

    // Four tiles moved about, four tiles still in the group, and the hand is whole.
    await expect(group.locator('.tile-button')).toHaveCount(4);
    await expect(tiles).toHaveCount(17);

    await closeAll([host]);
  });

  test('dragging a tile into the middle throws it', async ({ browser }) => {
    const { host } = await createRoom(browser);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    const page = host.page;
    await expect(page.getByTestId('turn-bar')).toBeVisible();

    // Lifted first. Dragging works either way, but with the hand down a drag straight up is also
    // the gesture that scrolls it, so the lifted state is the one worth pinning here.
    await page.getByTestId('toggle-hand').click();

    const tiles = page.getByTestId('my-hand').locator('.tile-button');
    const id = await tiles.first().getAttribute('data-tile-id');
    const source = page.getByTestId('my-hand').locator(`.tile-button[data-tile-id="${id}"]`);
    const pool = page.getByTestId('discard-pool');

    const from = (await source.boundingBox())!;
    const to = (await pool.boundingBox())!;

    await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
    await page.mouse.down();

    // Two moves: the first arms the drag past the threshold, the second lands it on the pool.
    await page.mouse.move(from.x + from.width / 2, from.y - 30, { steps: 4 });
    await page.mouse.move(to.x + to.width / 2, to.y + to.height / 2, { steps: 12 });

    // The zone says so before the tile is let go, otherwise nobody would know it was a target.
    await expect(pool).toHaveClass(/drop-hot/);

    await page.mouse.up();

    await expect(tiles).toHaveCount(16);
    await expect(page.locator('.discards mj-tile')).toHaveCount(1);

    // And it is the tile that was dragged, not whichever one sat in that position.
    const left = await tiles.evaluateAll((nodes) =>
      nodes.map((n) => n.getAttribute('data-tile-id')),
    );
    expect(left).not.toContain(id);

    await closeAll([host]);
  });

  test('the pool does not invite a drop when the throw would not be legal', async ({ browser }) => {
    const { host, code } = await createRoom(browser);
    const guest = await joinRoom(browser, code, 'Kuya Ben');
    await fillWithBots(host);
    await dealPlayable(host, [host, guest]);

    // Only one seat is mano, so at least one of the two humans is not the one to play. That player
    // may still rearrange - dragging is always on with Auto Arrange off - but the middle must not
    // offer to take a tile, because a throw out of turn is not a move.
    const page = (await host.page.getByTestId('turn-bar').isVisible()) ? guest.page : host.page;

    await page.getByTestId('toggle-hand').click();

    const tiles = page.getByTestId('my-hand').locator('.tile-button');
    const before = await tiles.count();
    const dragged = await tiles.first().getAttribute('data-tile-id');
    const pool = page.getByTestId('discard-pool');

    const from = (await tiles.first().boundingBox())!;
    const to = (await pool.boundingBox())!;

    await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
    await page.mouse.down();
    await page.mouse.move(from.x + from.width / 2, from.y - 30, { steps: 4 });
    await page.mouse.move(to.x + to.width / 2, to.y + to.height / 2, { steps: 12 });

    // The bots play on while this runs, so the turn can come round mid-drag. Only assert the
    // refusal while the state being tested still holds.
    if (!(await page.getByTestId('turn-bar').isVisible())) {
      await expect(pool).not.toHaveClass(/drop-live/);
      await expect(pool).not.toHaveClass(/drop-hot/);

      await page.mouse.up();

      // Nothing thrown, nothing lost.
      await expect(tiles).toHaveCount(before);
      expect(
        await tiles.evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-tile-id'))),
      ).toContain(dragged);
    } else {
      await page.mouse.up();
    }

    await closeAll([host, guest]);
  });

  test('a tile still goes up with one tap and a second while the hand is lifted', async ({ browser }) => {
    const { host } = await createRoom(browser);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    const page = host.page;
    await expect(page.getByTestId('turn-bar')).toBeVisible();

    // Lifting the hand does not change what a tap means: you lift it to decide what to throw, and
    // being made to drop it again first would be a step at the worst moment.
    await page.getByTestId('toggle-hand').click();

    const tiles = page.getByTestId('my-hand').locator('.tile-button');
    const id = await tiles.first().getAttribute('data-tile-id');
    const target = page.getByTestId('my-hand').locator(`.tile-button[data-tile-id="${id}"]`);

    await target.click();
    await target.click();

    // The second tap asks rather than throws, and the tile named in the question is the one that
    // was tapped - the whole point of the lifted hand is being able to see which that is.
    await expect(page.getByTestId('discard-confirm')).toBeVisible();
    await page.getByTestId('discard-go').click();

    await expect(tiles).toHaveCount(16);
    await expect(page.locator('.discards mj-tile')).toHaveCount(1);

    await closeAll([host]);
  });
});
