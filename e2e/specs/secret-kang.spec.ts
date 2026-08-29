import { expect, test } from '@playwright/test';
import {
  Player,
  closeAll,
  createRoom,
  dealPlayable,
  handSize,
  joinRoom,
  passAnyClaim,
} from './helpers';

/**
 * The Draw gate on your own declarations.
 *
 * KANG-RULE.md was filed as "it is my turn and I cannot lay my four 8 bamboo down". It was not a
 * bug: your turn opens in AwaitingDraw, and the whole turn-options object - Todas, secret kang,
 * sagasa, the lot - only exists once you have taken your tile. Nothing anywhere pinned that, so
 * the app was free to drift either way and the next player was free to file it again.
 *
 * Sloperama has a concealed kong declarable "anytime it's your turn", which is looser than this.
 * If that is ever adopted, this spec is the one that should fail and be rewritten - deliberately,
 * rather than by accident.
 *
 * A hand containing four of a face cannot be dealt on demand: the deal seed is Random.Shared in
 * GameService.DealCoreAsync with no way in. So this drives the gate itself, which is what the
 * report was actually about, and SecretKangOfferTests covers the four-of-a-face hand server-side.
 */

/** Throws whichever tile is at the end of the hand: lift, offer it up, confirm. */
async function throwLastTile(player: Player): Promise<void> {
  const last = player.page.getByTestId('my-hand').locator('.tile-button').last();

  await last.click();
  await last.click();
  await player.page.getByTestId('discard-go').click();
}

/**
 * Gets a seat that is not the mano to the point where the turn is theirs and they have not drawn.
 *
 * The mano throws, the other three say no thanks, and the tile goes dead - which hands the turn to
 * seat 1 in AwaitingDraw. Every pass is retried rather than fired once: the window opens on three
 * pages over a websocket and they do not all arrive in the same frame.
 */
async function passTheTurnTo(mano: Player, guests: Player[]): Promise<void> {
  await expect(mano.page.getByTestId('turn-bar')).toBeVisible();
  await throwLastTile(mano);

  await expect(async () => {
    for (const guest of guests) await passAnyClaim(guest);
    await expect(guests[0].page.getByTestId('draw-bar')).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 30_000 });
}

test.describe('declaring on your own turn', () => {
  test('a seat that has not drawn is offered no declarations, and drawing brings them', async ({
    browser,
  }) => {
    const { host, code } = await createRoom(browser);
    const guests = [
      await joinRoom(browser, code, 'Tito Ben'),
      await joinRoom(browser, code, 'Ate Rose'),
      await joinRoom(browser, code, 'Kuya Jun'),
    ];
    const everyone = [host, ...guests];

    await dealPlayable(host, everyone);

    // Seat 0 is the mano and starts holding the extra tile, so the seat under test is seat 1.
    const next = guests[0];
    await passTheTurnTo(host, guests);

    // The turn is theirs and the wall is theirs to take from. Nothing else is.
    const page = next.page;
    await expect(page.getByTestId('draw-bar')).toBeVisible();
    await expect(page.getByTestId('draw')).toBeEnabled();
    await expect(page.getByTestId('turn-bar')).toBeHidden();
    await expect(page.getByTestId('open-moves')).toBeHidden();
    await expect(page.getByTestId('declare-lifted')).toBeHidden();
    await expect(page.getByTestId('declare-todas')).toBeHidden();
    expect(await handSize(next)).toBe(16);

    await page.getByTestId('draw').click();

    // Drawing is what opens the turn. This is the step the bug report was missing.
    await expect(page.getByTestId('turn-bar')).toBeVisible();
    await expect(page.getByTestId('draw-bar')).toBeHidden();
    expect(await handSize(next)).toBe(17);

    await closeAll(everyone);
  });

  test('the mano is on a full turn straight off the deal, with no draw to take first', async ({
    browser,
  }) => {
    // The other half of the same rule. The mano is dealt seventeen and is already in
    // AwaitingDiscard, so a kang in that hand is declarable before anybody has drawn anything.
    const { host, code } = await createRoom(browser);
    const guests = [
      await joinRoom(browser, code, 'Tito Ben'),
      await joinRoom(browser, code, 'Ate Rose'),
      await joinRoom(browser, code, 'Kuya Jun'),
    ];
    const everyone = [host, ...guests];

    await dealPlayable(host, everyone);

    await expect(host.page.getByTestId('turn-bar')).toBeVisible();
    await expect(host.page.getByTestId('draw-bar')).toBeHidden();
    await expect(host.page.getByTestId('draw')).toBeDisabled();
    expect(await handSize(host)).toBe(17);

    await closeAll(everyone);
  });
});
