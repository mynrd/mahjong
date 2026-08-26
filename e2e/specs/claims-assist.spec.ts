import { Locator, Page, expect, test } from '@playwright/test';
import {
  Player,
  closeAll,
  createRoomWithRules,
  dealPlayable,
  drawIfOffered,
  fillWithBots,
  passAnyClaim,
  throwAnyTile,
} from './helpers';

/**
 * The claim window: which tiles are outlined, what happens when the player picks the wrong ones,
 * and that a picked group is the group that gets melded.
 *
 * The hand cannot be chosen. Nothing in the API seeds a specific deal, so rather than pretending
 * otherwise these specs play against three bots until a claim window happens to open, then assert
 * the things that hold for whatever the window turns out to be. The claim window itself is
 * stretched to sixty seconds through the house rules, because six is right for playing and far
 * too short to drive a browser through picking tiles and reading the refusal.
 */

const LONG_WINDOW = { claimWindowSeconds: 60 };

/**
 * Plays the host's turns against the bots until a claim window opens that actually offers
 * something.
 *
 * Every discard opens a window on every other seat, so most of them offer nothing at all and are
 * simply answered and played through. What these specs are about is the ones with options on them.
 */
async function playUntilClaim(player: Player, timeoutMs = 150_000): Promise<void> {
  const page = player.page;
  const claimBar = page.getByTestId('claim-bar');
  const turnBar = page.getByTestId('turn-bar');
  const outcome = page.getByTestId('outcome');

  // The draw prompt counts as something to do. Without it in this list the loop waits forty
  // seconds on a table that is only waiting for this seat to press Draw, which is every second
  // turn: nothing takes a tile off the wall by itself.
  const drawBar = page.getByTestId('draw-bar');

  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    await claimBar.or(turnBar).or(drawBar).or(outcome).first().waitFor({ state: 'visible', timeout: 40_000 });

    if (await outcome.isVisible()) throw new Error('The hand finished before a claim window opened.');

    if (await claimBar.isVisible()) {
      if ((await claimBar.locator('[data-claim-kind]').count()) > 0) return;

      // Nothing on offer here. Saying so is what lets the tile go and the game carry on.
      await passAnyClaim(player);
      continue;
    }

    if (await drawIfOffered(player)) continue;
    if (!(await turnBar.isVisible())) continue;

    // Never declare a win: the hand would end and there would be no more discards to claim.
    await throwAnyTile(player);
  }

  throw new Error(`No claim window opened within ${timeoutMs / 1000}s.`);
}

/** The hand tiles carrying a hint, as { id, face, hint, badge }. */
async function hintedTiles(page: Page) {
  return page
    .getByTestId('my-hand')
    .locator('.tile-button')
    .evaluateAll((nodes) =>
      nodes
        .map((node) => ({
          id: node.getAttribute('data-tile-id') ?? '',
          face: node.getAttribute('data-tile') ?? '',
          hint: node.querySelector('.tile')?.getAttribute('data-hint') ?? null,
          badge: node.querySelector('.badge')?.textContent?.trim() ?? null,
        }))
        .filter((tile) => tile.hint !== null),
    );
}

function tileById(page: Page, id: string): Locator {
  return page.getByTestId('my-hand').locator(`.tile-button[data-tile-id="${id}"]`);
}

test.describe('claiming a discard', () => {
  test('the tiles that could take the discard are outlined, badged and named', async ({ browser }) => {
    test.setTimeout(200_000);

    const { host } = await createRoomWithRules(browser, LONG_WINDOW);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    await playUntilClaim(host);

    const page = host.page;
    const candidates = page.getByTestId('claim-bar').locator('[data-claim-kind]');

    // playUntilClaim only stops on a window with options on it, so there is at least one button.
    expect(await candidates.count()).toBeGreaterThan(0);

    const hinted = await hintedTiles(page);
    const kinds = await candidates.evaluateAll((nodes) =>
      nodes.map((n) => n.getAttribute('data-claim-kind') ?? ''),
    );

    // Todas is read off the whole hand, so it names no tiles. Every other candidate does.
    if (kinds.some((k) => k !== 'Todas')) {
      expect(hinted.length).toBeGreaterThan(0);
    }

    // Not colour only: every outlined tile carries its letter, and says what it could do.
    for (const tile of hinted) {
      expect(['single', 'multi']).toContain(tile.hint);
      expect(['K', 'P', 'C']).toContain(tile.badge);

      const label = await tileById(page, tile.id).getAttribute('aria-label');
      expect(label).toMatch(/, can (kang|pung|chow)/);
    }

    // A tile is only ever marked multi when there really is more than one way to use it, which
    // means at least two candidates of the same shape are on offer.
    if (hinted.some((t) => t.hint === 'multi')) {
      expect(await candidates.count()).toBeGreaterThan(1);
    }

    // The first button is the one the server would award if everybody grabbed at once.
    await expect(candidates.first()).toHaveClass(/recommended/);

    await expect(page.getByTestId('claim-legend')).toBeVisible();

    // The whole page, because the point of the shot is the hand and the claim bar together: the
    // outlines only mean anything next to the buttons that explain them.
    await page.screenshot({ path: 'screenshots/claim-hints.png' });

    await closeAll([host]);
  });

  test('a pick that makes nothing is refused locally and never reaches the server', async ({ browser }) => {
    test.setTimeout(200_000);

    const { host } = await createRoomWithRules(browser, LONG_WINDOW);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    await playUntilClaim(host);

    const page = host.page;
    const hinted = await hintedTiles(page);
    test.skip(hinted.length === 0, 'This window only offers a todas, which names no tiles.');

    const before = await page.getByTestId('my-hand').locator('.tile-button').count();

    // --- one tile of a group, which is one short ---
    await tileById(page, hinted[0].id).click();

    await expect(page.getByTestId('claim-invalid')).toContainText('Pick one more tile');
    await expect(page.getByTestId('claim-take')).toBeDisabled();

    // Nothing was sent: the window is still open and the hand is untouched.
    await expect(page.getByTestId('claim-bar')).toBeVisible();
    await expect(page.getByTestId('my-hand').locator('.tile-button')).toHaveCount(before);

    // --- two tiles that make nothing ---
    const unhinted = await page
      .getByTestId('my-hand')
      .locator('.tile-button')
      .evaluateAll((nodes) =>
        nodes
          .filter((n) => n.querySelector('.tile')?.getAttribute('data-hint') === null)
          .map((n) => n.getAttribute('data-tile-id') ?? ''),
      );

    if (unhinted.length >= 2) {
      await tileById(page, hinted[0].id).click(); // unpick
      await tileById(page, unhinted[0]).click();
      await tileById(page, unhinted[1]).click();

      await expect(page.getByTestId('claim-invalid')).toContainText('not a valid move');
      await expect(page.getByTestId('claim-take')).toBeDisabled();
      await expect(page.getByTestId('my-hand').locator('.tile-button')).toHaveCount(before);
    }

    await closeAll([host]);
  });

  test('picking the right tiles takes the discard with exactly those tiles', async ({ browser }) => {
    test.setTimeout(200_000);

    const { host } = await createRoomWithRules(browser, LONG_WINDOW);
    await fillWithBots(host);
    await dealPlayable(host, [host]);

    await playUntilClaim(host);

    const page = host.page;
    const candidates = page.getByTestId('claim-bar').locator('[data-claim-kind]:not([data-claim-kind="Todas"])');

    test.skip((await candidates.count()) === 0, 'This window only offers a todas.');

    // Work out which tiles the first real candidate is built from by reading its label, which the
    // server writes: "Pung B5", "Chow B3-B4-B5". Off the attribute rather than the text, because
    // the option draws the set as tiles and their corner ranks are text too.
    const describe = (await candidates.first().getAttribute('data-claim-describe')) ?? '';

    const thrown = await page.getByTestId('claim-bar').locator('mj-tile .tile').first().getAttribute('data-code');

    const hinted = await hintedTiles(page);
    const wanted = wantedFaces(describe, thrown ?? '');

    const picks: string[] = [];
    const spare = [...hinted];

    for (const face of wanted) {
      const index = spare.findIndex((t) => t.face === face);
      expect(index, `no hinted ${face} for "${describe}"`).toBeGreaterThanOrEqual(0);
      picks.push(spare.splice(index, 1)[0].id);
    }

    for (const id of picks) await tileById(page, id).click();

    await expect(page.getByTestId('claim-invalid')).toHaveCount(0);
    await expect(page.getByTestId('claim-take')).toBeEnabled();
    await expect(page.getByTestId('claim-take')).toContainText(describe);

    await page.getByTestId('claim-take').click();

    // The meld lands in front of the player, built from exactly the tiles that were picked. Not
    // instantly: the window waits for the other three seats to answer before it resolves, and the
    // bots answer one game tick apart.
    const meld = page.locator('[data-testid="my-meld"]').last();
    await expect(meld).toBeVisible({ timeout: 30_000 });

    const melded = await meld
      .locator('mj-tile .tile')
      .evaluateAll((nodes) => nodes.map((n) => n.getAttribute('data-code') ?? ''));

    expect(melded.slice().sort()).toEqual([...wanted, thrown].sort());

    for (const id of picks) {
      await expect(
        page.getByTestId('my-hand').locator(`.tile-button[data-tile-id="${id}"]`),
      ).toHaveCount(0, { timeout: 30_000 });
    }

    await closeAll([host]);
  });
});

/**
 * The faces the candidate would take out of hand, read off the label the server wrote. "Pung B5"
 * with B5 thrown wants one more B5; "Chow B3-B4-B5" wants the B3 and the B4.
 */
function wantedFaces(describe: string, thrown: string): string[] {
  const faces = describe.match(/[DBC][1-9]/g) ?? [];

  if (describe.startsWith('Chow')) return faces.filter((f, i) => f !== thrown || faces.indexOf(f) !== i);

  // Pung and kang: the label names the face once, and the group takes one fewer than its size.
  const copies = describe.startsWith('Kang') ? 3 : 2;
  return Array.from({ length: copies }, () => thrown);
}
