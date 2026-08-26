import { Page, expect, test } from '@playwright/test';
import {
  Player,
  closeAll,
  createRoomWithRules,
  deal,
  dealPlayable,
  drawIfOffered,
  fillWithBots,
  joinRoom,
  passAnyClaim,
  throwAnyTile,
  waitForMyTurn,
} from './helpers';

/**
 * A table created with Allow Helper off. What is worth driving a browser for here is the half of
 * the feature that is about what is *not* on screen: no groups spelled out, no outlines on the
 * hand, no Auto Arrange, no countdown, and a claim strip that turns up for every seat rather than
 * only the ones holding something. The rules underneath it - a press that nothing times out,
 * the way back out of one, the next seat drawing to end the window - are pinned down in
 * AssistOffTests.cs, where the hands can be dealt on purpose instead of hoped for.
 *
 * The joker is turned off for the same reason it is off in the domain tests: a random wild face
 * makes "this tile is not marked" ambiguous.
 */
const MANUAL = { assistEnabled: false, jokerEnabled: false };

/**
 * Deals and plays the host's turns against the bots until a claim window opens.
 *
 * Nothing in the API seeds a deal, so which discards this seat can act on is luck. Two things can
 * go wrong before a window ever reaches the host: the hand can end first, and it can end on a bot's
 * win rather than anything the host did. Both are ordinary, so the hand is simply dealt again.
 */
async function dealUntilClaim(host: Player, options: ClaimWanted = {}, attempts = 4): Promise<void> {
  for (let attempt = 1; ; attempt++) {
    await dealPlayable(host, [host]);

    if (await playUntilClaim(host, options)) return;

    if (attempt >= attempts)
      throw new Error(`${attempts} hands in a row ended before a claim window reached the host.`);

    await host.page.getByTestId('back-to-lobby').click();
    await expect(host.page.getByTestId('start-hand')).toBeEnabled();
  }
}

/**
 * What kind of claim window a spec is waiting for. Every discard opens one on every other seat, so
 * `withOptions` is how a spec asks for one that actually has something on it - which only an
 * assisted table ever draws, and only when the hand happens to hold the right tiles.
 */
interface ClaimWanted {
  withOptions?: boolean;
}

/** Plays the host's turns until a claim window opens. False if the hand ended first. */
async function playUntilClaim(
  player: Player,
  wanted: ClaimWanted = {},
  timeoutMs = 150_000,
): Promise<boolean> {
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

    if (await outcome.isVisible()) return false;

    if (await claimBar.isVisible()) {
      if (!wanted.withOptions || (await claimBar.locator('.option').count()) > 0) return true;

      await passAnyClaim(player);
      continue;
    }

    if (await drawIfOffered(player)) continue;
    if (!(await turnBar.isVisible())) continue;

    await throwAnyTile(player);
  }

  throw new Error(`No claim window opened within ${timeoutMs / 1000}s.`);
}

/** How many tiles in the hand carry a coloured outline. Must be zero at an unassisted table. */
async function hintedCount(page: Page): Promise<number> {
  return page
    .getByTestId('my-hand')
    .locator('.tile-button')
    .evaluateAll(
      (nodes) => nodes.filter((n) => n.querySelector('.tile')?.getAttribute('data-hint') === 'single'
        || n.querySelector('.tile')?.getAttribute('data-hint') === 'multi'
        || n.querySelector('.tile')?.getAttribute('data-hint') === 'win').length,
    );
}

test.describe('a table with Allow Helper off', () => {
  test('offers the calls it can and spells none of them out', async ({ browser }) => {
    test.setTimeout(200_000);

    const { host, code } = await createRoomWithRules(browser, MANUAL);
    const players = [host];

    try {
      await fillWithBots(host);
      await dealUntilClaim(host);

      // Auto Arrange is help too, so the chip is not on the table at all.
      await expect(host.page.getByTestId('auto-arrange')).toHaveCount(0);

      const dialog = host.page.getByTestId('claim-bar');

      // The calls, and nothing that says which of them this hand could actually make.
      await expect(host.page.getByTestId('claim-calls')).toBeVisible();
      for (const kind of ['Pung', 'Kang', 'Todas'])
        await expect(host.page.getByTestId(`claim-call-${kind}`)).toBeVisible();

      // Chow is the one call that is not always there, and hiding it gives nothing away: who threw
      // the tile and what it is are on the table for all four to see. The host sits in seat 0, so a
      // chow is open to it only off seat 3, and only on a suited tile.
      const from = Number(await dialog.getAttribute('data-from-seat'));
      const thrown = await dialog.locator('[data-code]').first().getAttribute('data-code');
      const couldChow = (from + 1) % 4 === 0 && /^[DBC]/.test(thrown ?? '');

      await expect(host.page.getByTestId('claim-call-Chow')).toHaveCount(couldChow ? 1 : 0);

      await expect(dialog.locator('.option.recommended')).toHaveCount(0);
      await expect(dialog.locator('.best')).toHaveCount(0);
      await expect(dialog.locator('.option .combo')).toHaveCount(0);

      // Every discard here came from a bot, and none of those is on a clock: the table holds
      // still until the people at it have looked at the tile and said they are done with it.
      await expect(host.page.getByTestId('claim-countdown')).toHaveText('no rush');

      // And nothing in the hand is marked.
      expect(await hintedCount(host.page)).toBe(0);
    } finally {
      await closeAll(players);
    }

    expect(code).toMatch(/^[A-Z0-9]{6}$/);
  });

  test('pressing a call either asks for the tiles or is refused, never silently ignored', async ({
    browser,
  }) => {
    test.setTimeout(200_000);

    const { host } = await createRoomWithRules(browser, MANUAL);
    const players = [host];

    try {
      await fillWithBots(host);
      await dealUntilClaim(host);

      await host.page.getByTestId('claim-call-Pung').click();

      // Which of the two happens depends on tiles nobody can choose. Both are the button being
      // wired to the server; what would be a bug is neither.
      const naming = host.page.getByTestId('claim-naming');
      const refused = host.page.getByTestId('claim-refused');
      const other = host.page.getByTestId('move-error');

      await naming.or(refused).or(other).first().waitFor({ state: 'visible', timeout: 15_000 });

      if (await naming.isVisible()) {
        // The press was accepted. Nothing is counting against it - the tile is the host's until it
        // names the tiles or lets the call go - and Take stays dead until the right number of them
        // is tapped.
        await expect(naming).toContainText('two tiles');
        await expect(host.page.getByTestId('claim-countdown')).toHaveText('no rush');
        await expect(host.page.getByTestId('claim-take')).toBeDisabled();

        // The calls are gone: the only thing left to do is name the tiles.
        await expect(host.page.getByTestId('claim-calls')).toHaveCount(0);

        // Unless it is taken back, which is the way out of a call pressed on a hand that turns out
        // not to pay for it. The dialog goes back to what it opened with.
        await host.page.getByTestId('claim-cancel').click();

        await expect(host.page.getByTestId('claim-calls')).toBeVisible();
        await expect(host.page.getByTestId('claim-naming')).toHaveCount(0);
        await expect(host.page.getByTestId('claim-countdown')).toHaveText('no rush');
      } else if (await refused.isVisible()) {
        // A no that can be looked at rather than read: the two tiles the pung would have taken,
        // drawn at the size of the ones in hand.
        await expect(refused).toContainText('cannot pung');
        await expect(refused.locator('[data-code]')).toHaveCount(2);
      }
    } finally {
      await closeAll(players);
    }
  });

  test('a bot’s discard waits for the person, and Next is what lets it go', async ({ browser }) => {
    test.setTimeout(200_000);

    const { host } = await createRoomWithRules(browser, MANUAL);
    const players = [host];

    try {
      await fillWithBots(host);
      await dealUntilClaim(host);

      // Nothing counts a window down any more, whoever threw the tile: three bots would happily
      // play the rest of the hand in the time it takes to read one tile, and the whole point of
      // this window is that they do not. It is still there ten seconds later.
      await host.page.waitForTimeout(11_000);
      await expect(host.page.getByTestId('claim-bar')).toBeVisible();
      await expect(host.page.getByTestId('claim-countdown')).toHaveText('no rush');

      // And the button that ends it is still there to press.
      await host.page.getByTestId('claim-pass').click();

      await expect(host.page.getByTestId('claim-bar')).toBeHidden({ timeout: 20_000 });
      await expect(host.page.getByTestId('claim-strip')).toBeHidden({ timeout: 20_000 });
    } finally {
      await closeAll(players);
    }
  });

  test('a human’s discard has no clock, and the next seat can move the game on', async ({ browser }) => {
    test.setTimeout(200_000);

    // Two people and two bots, so there is a human throwing tiles and a human sitting after them.
    const { host, code } = await createRoomWithRules(browser, MANUAL);
    const guest = await joinRoom(browser, code, 'Tito Ben');
    const players = [host, guest];

    try {
      await fillWithBots(host);
      await deal(host, players);

      const strip = guest.page.getByTestId('claim-strip');

      // Specifically a window on something the host threw, because the guest sits immediately
      // after the host and so is the seat that would pick the tile up if it went dead.
      const fromHost = guest.page.getByTestId('claim-bar').filter({ hasText: `${host.name} threw` });

      const deadline = Date.now() + 150_000;

      // The host answers anything put in front of it and keeps throwing, so the table does not sit
      // waiting on a page nobody is driving. The guest does nothing at all: what is being tested is
      // that a window on a person's discard sits there until the guest itself moves the game on.
      while (Date.now() < deadline && !(await fromHost.isVisible())) {
        await passAnyClaim(host);
        await drawIfOffered(host);

        if (await host.page.getByTestId('turn-bar').isVisible().catch(() => false)) {
          await throwAnyTile(host);
        }

        await fromHost.waitFor({ state: 'visible', timeout: 2_000 }).catch(() => undefined);
      }

      await expect(fromHost).toBeVisible();

      // A person threw it, so nothing is counting down.
      await expect(guest.page.getByTestId('claim-countdown')).toHaveText('no rush');

      // Drawing is what ends a window nothing is timing, and the guest is the seat that may.
      await expect(guest.page.getByTestId('draw')).toBeEnabled();
      await guest.page.getByTestId('draw').click();

      await expect(strip).toBeHidden({ timeout: 20_000 });
    } finally {
      await closeAll(players);
    }
  });

  test('an assisted table still spells the options out', async ({ browser }) => {
    test.setTimeout(200_000);

    // The same spec against the opposite setting, so a regression that quietly turned assist off
    // everywhere would fail here rather than looking like a pass above.
    const { host } = await createRoomWithRules(browser, {
      assistEnabled: true,
      jokerEnabled: false,
      claimWindowSeconds: 60,
    });
    const players = [host];

    try {
      await fillWithBots(host);
      await dealUntilClaim(host, { withOptions: true });

      await expect(host.page.getByTestId('auto-arrange')).toBeVisible();
      await expect(host.page.getByTestId('claim-calls')).toHaveCount(0);
      await expect(host.page.getByTestId('claim-bar').locator('.option')).not.toHaveCount(0);
    } finally {
      await closeAll(players);
    }
  });
});
