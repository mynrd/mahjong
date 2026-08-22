import { Page, expect, test } from '@playwright/test';
import { closeAll, createRoom, dealPlayable, fillWithBots, handSize } from './helpers';

/**
 * Auto Arrange is display only: it changes where the tiles sit, never which tiles they are or
 * which one a tap throws. These specs pin exactly that, because a layout change that quietly
 * altered the hand would be the worst kind of bug in this feature - invisible until somebody
 * threw the wrong tile.
 */

/** The ids of the tiles in the hand, in the order they are drawn. */
async function handIds(page: Page): Promise<string[]> {
  return page
    .getByTestId('my-hand')
    .locator('.tile-button')
    .evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-tile-id') ?? ''));
}

async function blockKinds(page: Page): Promise<string[]> {
  return page
    .getByTestId('my-hand')
    .locator('.group')
    .evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-group') ?? ''));
}

/**
 * Presses the toggle and waits for the layout to actually change. Reading the DOM straight after
 * the click races the re-render: the signal is set synchronously but the tiles have not moved yet.
 */
async function setArrange(page: Page, on: boolean): Promise<void> {
  const toggle = page.getByTestId('auto-arrange');

  if ((await toggle.getAttribute('aria-pressed')) !== String(on)) await toggle.click();

  await expect(toggle).toHaveAttribute('aria-pressed', String(on));
  await expect(page.getByTestId('my-hand').locator('.group[data-group="all"]')).toHaveCount(on ? 0 : 1);
}

test.describe('auto arrange', () => {
  test('grouping the hand keeps every tile and adds blocks', async ({ browser }) => {
    const { host } = await createRoom(browser);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    await expect(host.page.getByTestId('my-hand').locator('.tile-button')).toHaveCount(17);

    // Off: one block holding the whole hand.
    expect(await blockKinds(host.page)).toEqual(['all']);
    const sorted = await handIds(host.page);

    await setArrange(host.page, true);

    // On: the same seventeen tiles, in more than one block.
    expect(await handSize(host)).toBe(17);

    const kinds = await blockKinds(host.page);
    expect(kinds.length).toBeGreaterThan(1);
    expect(kinds).not.toContain('all');

    // Every block is one of the six kinds the arranger produces, and nothing was invented.
    for (const kind of kinds) {
      expect(['Kang', 'Pung', 'Chow', 'Pair', 'Partial', 'Floater']).toContain(kind);
    }

    const grouped = await handIds(host.page);
    expect(grouped.slice().sort()).toEqual(sorted.slice().sort());

    // A partial block always says what would finish it, so the grouping is readable without
    // seeing the gaps between blocks.
    const partials = host.page.getByTestId('my-hand').locator('.group[data-group="Partial"]');
    for (let i = 0; i < (await partials.count()); i++) {
      await expect(partials.nth(i).locator('.group-label')).toContainText('needs');
    }

    await host.page.screenshot({ path: 'screenshots/auto-arrange.png' });

    // Off again: back to one block in suit order.
    await setArrange(host.page, false);
    expect(await blockKinds(host.page)).toEqual(['all']);
    expect(await handIds(host.page)).toEqual(sorted);

    await closeAll([host]);
  });

  test('a block holds the tiles its label claims it does', async ({ browser }) => {
    const { host } = await createRoom(browser);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    await setArrange(host.page, true);

    // A joker sitting inside a group keeps its own face, so it is taken out before the shape of
    // the group is checked - otherwise a pung completed by a joker looks like two different faces.
    const joker = await host.page.getByTestId('joker').locator('.tile').getAttribute('data-code');

    const blocks = host.page.getByTestId('my-hand').locator('.group');

    for (let i = 0; i < (await blocks.count()); i++) {
      const block = blocks.nth(i);
      const kind = await block.getAttribute('data-group');
      const all = await block
        .locator('.tile-button')
        .evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-tile') ?? ''));

      const faces = all.filter((f) => f !== joker);

      switch (kind) {
        case 'Kang':
          expect(all).toHaveLength(4);
          expect(new Set(faces).size).toBeLessThanOrEqual(1);
          break;

        case 'Pung':
          expect(all).toHaveLength(3);
          expect(new Set(faces).size).toBeLessThanOrEqual(1);
          break;

        case 'Chow': {
          expect(all).toHaveLength(3);
          expect(new Set(faces.map((f) => f[0])).size).toBeLessThanOrEqual(1);

          const ranks = faces.map((f) => Number(f.slice(1)));
          expect(Math.max(...ranks) - Math.min(...ranks)).toBeLessThanOrEqual(2);
          break;
        }

        case 'Pair':
          expect(all).toHaveLength(2);
          expect(new Set(faces).size).toBeLessThanOrEqual(1);
          break;

        case 'Partial': {
          // Two real tiles of one suit, one or two ranks apart. A joker never lands in a partial:
          // a joker next to a tile is already a pair, which is a better reading of the same two.
          expect(all).toHaveLength(2);
          expect(faces).toHaveLength(2);
          expect(faces[0][0]).toBe(faces[1][0]);

          const gap = Math.abs(Number(faces[0].slice(1)) - Number(faces[1].slice(1)));
          expect([1, 2]).toContain(gap);
          break;
        }

        case 'Floater':
          expect(all).toHaveLength(1);
          break;

        default:
          throw new Error(`Unexpected block kind ${kind}.`);
      }
    }

    await closeAll([host]);
  });

  test('the toggle survives a reload', async ({ browser }) => {
    const { host } = await createRoom(browser);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    await setArrange(host.page, true);

    await host.page.reload();
    await expect(host.page.getByTestId('table')).toBeVisible({ timeout: 20_000 });

    await expect(host.page.getByTestId('auto-arrange')).toHaveAttribute('aria-pressed', 'true');
    expect(await blockKinds(host.page)).not.toEqual(['all']);

    await closeAll([host]);
  });

  test('the tile that leaves the hand is the one that was tapped, not the one in that position', async ({
    browser,
  }) => {
    const { host } = await createRoom(browser);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    await setArrange(host.page, true);
    await expect(host.page.getByTestId('turn-bar')).toBeVisible();

    // The last tile of the last block, which after grouping is rarely the tile that sat last in
    // suit order. Pinned by id rather than by position, so the two taps cannot land on different
    // tiles if the hand re-renders between them.
    const tiles = host.page.getByTestId('my-hand').locator('.tile-button');
    const thrown = await tiles.last().getAttribute('data-tile-id');
    const target = host.page.getByTestId('my-hand').locator(`.tile-button[data-tile-id="${thrown}"]`);

    await target.click();
    await target.click();

    await expect(tiles).toHaveCount(16);
    expect(await handIds(host.page)).not.toContain(thrown);

    // And it is in the pool, so it really was thrown rather than lost.
    await expect(host.page.locator('.discards mj-tile')).toHaveCount(1);

    await closeAll([host]);
  });
});
