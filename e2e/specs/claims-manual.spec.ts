import { Page, expect, test } from '@playwright/test';
import {
  Player,
  closeAll,
  createRoomWithRules,
  deal,
  dealPlayable,
  fillWithBots,
  joinRoom,
  waitForMyTurn,
} from './helpers';

/**
 * A table created with Allow Helper off. What is worth driving a browser for here is the half of
 * the feature that is about what is *not* on screen: no groups spelled out, no outlines on the
 * hand, no Auto Arrange, no countdown, and a claim strip that turns up for every seat rather than
 * only the ones holding something. The rules underneath it - the ten-second naming clock, one
 * press per discard, the next seat drawing to end the window - are pinned down in
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
async function dealUntilClaim(host: Player, attempts = 4): Promise<void> {
  for (let attempt = 1; ; attempt++) {
    await dealPlayable(host, [host]);

    if (await playUntilClaim(host)) return;

    if (attempt >= attempts)
      throw new Error(`${attempts} hands in a row ended before a claim window reached the host.`);

    await host.page.getByTestId('back-to-lobby').click();
    await expect(host.page.getByTestId('start-hand')).toBeEnabled();
  }
}

/** Plays the host's turns until a claim window opens. False if the hand ended first. */
async function playUntilClaim(player: Player, timeoutMs = 150_000): Promise<boolean> {
  const page = player.page;
  const claimBar = page.getByTestId('claim-bar');
  const turnBar = page.getByTestId('turn-bar');
  const outcome = page.getByTestId('outcome');

  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    await claimBar.or(turnBar).or(outcome).first().waitFor({ state: 'visible', timeout: 40_000 });

    if (await claimBar.isVisible()) return true;
    if (await outcome.isVisible()) return false;
    if (!(await turnBar.isVisible())) continue;

    const last = page.getByTestId('my-hand').locator('.tile-button').last();
    if (!(await last.isEnabled().catch(() => false))) continue;

    await last.click({ timeout: 5_000 }).catch(() => undefined);
    await last.click({ timeout: 5_000 }).catch(() => undefined);
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
  test('offers the four calls and spells none of them out', async ({ browser }) => {
    test.setTimeout(200_000);

    const { host, code } = await createRoomWithRules(browser, MANUAL);
    const players = [host];

    try {
      await fillWithBots(host);
      await dealUntilClaim(host);

      // Auto Arrange is help too, so the chip is not on the table at all.
      await expect(host.page.getByTestId('auto-arrange')).toHaveCount(0);

      const dialog = host.page.getByTestId('claim-bar');

      // The four calls, and nothing that says which of them this hand could actually make.
      await expect(host.page.getByTestId('claim-calls')).toBeVisible();
      for (const kind of ['Chow', 'Pung', 'Kang', 'Todas'])
        await expect(host.page.getByTestId(`claim-call-${kind}`)).toBeVisible();

      await expect(dialog.locator('.option.recommended')).toHaveCount(0);
      await expect(dialog.locator('.best')).toHaveCount(0);
      await expect(dialog.locator('.option .combo')).toHaveCount(0);

      // Every discard here came from a bot, and those are on a clock: nobody at this table is
      // going to say "your turn, taking it or not".
      await expect(host.page.getByTestId('claim-countdown')).toHaveText(/^\d+s$/);

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
      const refused = host.page.getByTestId('move-error');

      await naming.or(refused).first().waitFor({ state: 'visible', timeout: 15_000 });

      if (await naming.isVisible()) {
        // The press was accepted, so the ten seconds to name the tiles is now running and the
        // Take button stays dead until the right number of them is tapped.
        await expect(naming).toContainText('two tiles');
        await expect(host.page.getByTestId('claim-countdown')).toHaveText(/^\d+s$/);
        await expect(host.page.getByTestId('claim-take')).toBeDisabled();

        // The calls are gone: the press is spent and the only thing left to do is name the tiles.
        await expect(host.page.getByTestId('claim-calls')).toHaveCount(0);
      }
    } finally {
      await closeAll(players);
    }
  });

  test('a bot’s discard dies on its own, with nobody answering it', async ({ browser }) => {
    test.setTimeout(200_000);

    const { host } = await createRoomWithRules(browser, MANUAL);
    const players = [host];

    try {
      await fillWithBots(host);
      await dealUntilClaim(host);

      // Nothing is pressed. The ten seconds run out, the tile dies and the bots play on, which is
      // the whole reason a bot's discard carries a deadline when a human's does not.
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

      // Specifically a window on something the host threw. The bots throw too, and theirs are the
      // ones that do carry a clock, so a strip on its own proves nothing here.
      const fromHost = guest.page.getByTestId('claim-bar').filter({ hasText: `${host.name} threw` });

      const deadline = Date.now() + 150_000;

      // A window only opens if somebody can actually take the tile, which is luck. Both people
      // keep playing until one of the host's discards is claimable by somebody. The host answers
      // anything put in front of it so the table does not sit waiting on a page nobody is driving.
      while (Date.now() < deadline && !(await fromHost.isVisible())) {
        for (const player of [host, guest]) {
          const pass = player.page.getByTestId('claim-pass').first();

          if (player === host && (await pass.isVisible().catch(() => false))) {
            await pass.click({ timeout: 5_000 }).catch(() => undefined);
            continue;
          }

          if (!(await player.page.getByTestId('turn-bar').isVisible().catch(() => false))) continue;

          const last = player.page.getByTestId('my-hand').locator('.tile-button').last();
          await last.click({ timeout: 5_000 }).catch(() => undefined);
          await last.click({ timeout: 5_000 }).catch(() => undefined);
        }

        await fromHost.waitFor({ state: 'visible', timeout: 2_000 }).catch(() => undefined);
      }

      await expect(fromHost).toBeVisible();

      // A person threw it, so nothing is counting down.
      await expect(guest.page.getByTestId('claim-countdown')).toHaveText('no rush');

      // The guest sits immediately after the host, so it is the seat that would pick up if the
      // tile went dead, and the only one offered the way out of a window with no deadline.
      await expect(guest.page.getByTestId('claim-draw').first()).toBeVisible();
      await guest.page.getByTestId('claim-draw').first().click();

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
      await dealUntilClaim(host);

      await expect(host.page.getByTestId('auto-arrange')).toBeVisible();
      await expect(host.page.getByTestId('claim-calls')).toHaveCount(0);
      await expect(host.page.getByTestId('claim-bar').locator('.option')).not.toHaveCount(0);
      await expect(host.page.getByTestId('claim-countdown')).toHaveText(/^\d+s$/);
    } finally {
      await closeAll(players);
    }
  });
});
