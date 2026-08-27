import { expect, test } from '@playwright/test';
import {
  closeAll,
  createRoom,
  deal,
  fillWithBots,
  joinRoom,
  playUntilOutcome,
} from './helpers';

/**
 * The offer of another game, and the three ways a seat can answer it: yes, no, and nothing at all.
 *
 * A finished hand used to deal again the moment the host pressed Start, over the top of whatever
 * anybody else wanted. Now it asks, and the asking is the thing being tested: the table deals only
 * when every seat is taken and every seat has said yes.
 *
 * Both tests pay for a whole played-out hand to get to the only phase where any of this exists.
 */
test.describe('calling another game', () => {
  test('the table deals once every seat has said yes', async ({ browser }) => {
    test.setTimeout(300_000);

    const { host, code } = await createRoom(browser);
    const guest = await joinRoom(browser, code, 'Guest');

    await fillWithBots(host);
    await deal(host, [host, guest]);
    await playUntilOutcome([host, guest]);

    // Nothing is on offer until somebody offers it, and only the host may.
    await expect(guest.page.getByTestId('new-game')).toBeHidden();
    await expect(guest.page.getByTestId('propose-new-game')).toBeHidden();

    await host.page.getByTestId('propose-new-game').click();

    // The result sheet gets out of the way by itself: the question is on the table behind it.
    const offer = guest.page.getByTestId('new-game');
    await expect(offer).toBeVisible({ timeout: 15_000 });
    await expect(host.page.getByTestId('new-game')).toBeVisible();

    // Calling it is agreeing to it, and the bots are in as they have no screen to be asked on.
    await expect(host.page.getByTestId('answer-0-state')).toHaveText('in');
    await expect(guest.page.getByTestId('answer-2-state')).toHaveText('in');
    await expect(guest.page.getByTestId('answer-3-state')).toHaveText('in');

    // The one seat holding the table up, and everybody can see which it is.
    await expect(host.page.getByTestId('answer-1-state')).toHaveText('still deciding');
    await expect(guest.page.getByTestId('answer-1-state')).toHaveText('still deciding');

    // The caller waits with everybody else: the host has no way to deal over the top of this.
    await expect(host.page.getByTestId('table')).toBeVisible();
    await expect(host.page.getByTestId('outcome-title')).toBeHidden();

    await guest.page.screenshot({ path: 'screenshots/new-game-offer.png', fullPage: true });

    await guest.page.getByTestId('join-new-game').click();

    // The last yes deals. Both pages land in a fresh hand: the offer is gone, so is the result of
    // the hand before it, and there are tiles to play with again.
    for (const player of [host, guest]) {
      await expect(player.page.getByTestId('new-game')).toBeHidden({ timeout: 20_000 });
      await expect(player.page.getByTestId('outcome')).toBeHidden();
      await expect(player.page.getByTestId('hand-number')).toHaveText('Hand 2');
    }

    await closeAll([host, guest]);
  });

  test('saying no is leaving, and an empty seat holds the table up until it is filled', async ({
    browser,
  }) => {
    test.setTimeout(300_000);

    const { host, code } = await createRoom(browser);
    const leaver = await joinRoom(browser, code, 'Leaver');
    const quiet = await joinRoom(browser, code, 'Quiet');

    await fillWithBots(host);
    await deal(host, [host, leaver, quiet]);
    await playUntilOutcome([host, leaver, quiet]);

    await host.page.getByTestId('propose-new-game').click();
    await expect(leaver.page.getByTestId('new-game')).toBeVisible({ timeout: 15_000 });

    // ---------------------------------------------------------------- no, thank you

    await leaver.page.getByTestId('decline-new-game').click();

    // Saying no is leaving. The page says so and stays put rather than dropping the player onto a
    // home screen with no explanation.
    await expect(leaver.page.getByTestId('removed')).toBeVisible({ timeout: 15_000 });
    await expect(leaver.page.getByTestId('removed-rejoin')).toBeVisible();

    // The chair they were in is empty now, and everybody still at the table can see it.
    await expect(host.page.getByTestId('answer-1-state')).toHaveText('nobody sitting', {
      timeout: 15_000,
    });
    await expect(quiet.page.getByTestId('answer-1-state')).toHaveText('nobody sitting');

    // ---------------------------------------------------------------- the seat that never answers

    // Seat 2 has not said anything at all. That is what the host's Remove is for, and it is only
    // ever offered on a seat that has not agreed.
    await expect(host.page.getByTestId('answer-2-state')).toHaveText('still deciding');
    await host.page.getByTestId('remove-2').click();

    await expect(quiet.page.getByTestId('removed')).toBeVisible({ timeout: 15_000 });
    await expect(host.page.getByTestId('answer-2-state')).toHaveText('nobody sitting', {
      timeout: 15_000,
    });

    // Two empty chairs, and the host is the only seat left that has agreed - so nothing has dealt.
    await expect(host.page.getByTestId('outcome')).toBeHidden();
    await expect(host.page.getByTestId('new-game')).toBeVisible();

    await host.page.screenshot({ path: 'screenshots/new-game-empty-seats.png', fullPage: true });

    // ---------------------------------------------------------------- filling the empty seats

    // Bots agree as they sit down, so filling the last empty chair of an otherwise agreed table
    // deals on the spot.
    await host.page.getByTestId('fill-with-bots').click();

    await expect(host.page.getByTestId('new-game')).toBeHidden({ timeout: 20_000 });
    await expect(host.page.getByTestId('hand-number')).toHaveText('Hand 2');

    await closeAll([host, leaver, quiet]);
  });
});
