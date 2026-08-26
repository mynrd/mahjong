import { Browser, BrowserContext, Page, expect } from '@playwright/test';
import { apiBaseUrlFor } from './urls';

export const PASSWORD = 'mahjong1';

/** Where the app keeps the seat token, so a spec can sit down without going through the form. */
const SEAT_KEY = 'mahjong.seat';

/** One seated player: their own browser context, so they get their own localStorage token. */
export interface Player {
  name: string;
  context: BrowserContext;
  page: Page;
}

/** Creates a table and returns the host, already sitting in seat 0, plus the room code. */
export async function createRoom(
  browser: Browser,
  options: { hostName?: string; roomName?: string; password?: string } = {},
): Promise<{ host: Player; code: string }> {
  const name = options.hostName ?? 'Mynard';
  const context = await browser.newContext();
  const page = await context.newPage();

  await page.goto('/');
  await page.getByTestId('room-name').fill(options.roomName ?? 'Sunday game');
  await page.getByTestId('room-password').fill(options.password ?? PASSWORD);
  await page.getByTestId('display-name').fill(name);
  await page.getByTestId('create-submit').click();

  await expect(page).toHaveURL(/\/room\/[A-Z0-9]{6}$/);
  const code = page.url().split('/').pop()!;

  return { host: { name, context, page }, code };
}

/**
 * Creates a table straight through the API so house rules can be set, then drops the seat token
 * into a fresh browser context and lands on the lobby exactly as the create form would have.
 *
 * The create form does not offer the rules, and the one rule the claim specs need to change is
 * the length of the claim window: six seconds is right for playing and far too short to drive a
 * browser through picking tiles, reading a message and undoing the pick.
 */
export async function createRoomWithRules(
  browser: Browser,
  rules: Record<string, unknown>,
  options: { hostName?: string; roomName?: string; password?: string } = {},
): Promise<{ host: Player; code: string }> {
  const name = options.hostName ?? 'Mynard';
  const webUrl = process.env.WEB_URL ?? 'http://localhost:5080';

  const response = await fetch(`${apiBaseUrlFor(webUrl)}/api/rooms`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      name: options.roomName ?? 'Sunday game',
      password: options.password ?? PASSWORD,
      displayName: name,
      rules,
    }),
  });

  if (!response.ok) throw new Error(`Creating the room failed: HTTP ${response.status}`);

  const seated = (await response.json()) as {
    roomCode: string;
    playerId: string;
    seat: number;
    playerToken: string;
    isHost: boolean;
  };

  const context = await browser.newContext();
  const stored = {
    roomCode: seated.roomCode,
    playerId: seated.playerId,
    seat: seated.seat,
    token: seated.playerToken,
    displayName: name,
    isHost: seated.isHost,
  };

  await context.addInitScript(
    ([key, value]) => window.localStorage.setItem(key, value),
    [SEAT_KEY, JSON.stringify(stored)] as const,
  );

  const page = await context.newPage();
  await page.goto(`/room/${seated.roomCode}`);
  await expect(page.getByTestId('start-hand')).toBeVisible();

  return { host: { name, context, page }, code: seated.roomCode };
}

/** Follows an invite link in a fresh context and takes a seat. */
export async function joinRoom(
  browser: Browser,
  code: string,
  name: string,
  password: string = PASSWORD,
): Promise<Player> {
  const context = await browser.newContext();
  const page = await context.newPage();

  await page.goto(`/join/${code}`);
  await page.getByTestId('join-name').fill(name);
  await page.getByTestId('join-password').fill(password);
  await page.getByTestId('join-submit').click();

  await expect(page).toHaveURL(new RegExp(`/room/${code}$`));

  return { name, context, page };
}

/** Fills the remaining seats with bots. Only the host may do this. */
export async function fillWithBots(host: Player): Promise<void> {
  await host.page.getByTestId('add-bots').click();
  await expect(host.page.getByTestId('start-hand')).toBeEnabled();
}

/** Deals, and waits for every given player to land on the table. */
export async function deal(host: Player, everyone: Player[]): Promise<void> {
  await host.page.getByTestId('start-hand').click();

  for (const player of everyone) {
    await expect(player.page).toHaveURL(/\/table$/, { timeout: 20_000 });
    await expect(player.page.getByTestId('table')).toBeVisible();
  }
}

/**
 * Deals, and deals again if the hand ended before anybody played a tile.
 *
 * About one deal in four hundred is a bisaklat: the mano is dealt a hand that is already complete,
 * the outcome sheet goes up straight away and there is no hand to test against. Rare enough to be
 * a surprise, common enough to have turned up twice in one afternoon of running this suite.
 */
export async function dealPlayable(host: Player, everyone: Player[], attempts = 3): Promise<void> {
  for (let attempt = 1; ; attempt++) {
    await deal(host, everyone);

    const outcome = host.page.getByTestId('outcome');
    if (!(await outcome.isVisible())) return;

    if (attempt >= attempts) throw new Error(`${attempts} deals in a row ended before a tile was played.`);

    await host.page.getByTestId('back-to-lobby').click();
    await expect(host.page.getByTestId('start-hand')).toBeEnabled();
  }
}

/** How many tiles this player can see in their own hand. */
export async function handSize(player: Player): Promise<number> {
  return player.page.getByTestId('my-hand').locator('.tile-button').count();
}

/**
 * Every tile face this page can actually read, taken from the DOM rather than from the network.
 * Used to prove one player cannot see another's tiles even by inspecting the page.
 */
export async function visibleTileCodes(page: Page): Promise<string[]> {
  return page.locator('mj-tile .tile[data-code]').evaluateAll((nodes) =>
    nodes.map((n) => n.getAttribute('data-code') ?? ''),
  );
}

export async function closeAll(players: Player[]): Promise<void> {
  for (const player of players) await player.context.close();
}

/**
 * Presses Draw if the wall is this player's to take from right now, and says whether it did.
 *
 * Nothing takes a tile off the wall by itself any more, so a spec that plays a hand has to press
 * this or the table sits on the player's turn forever. The button is always on the bar; whether it
 * is enabled is the whole question.
 */
export async function drawIfOffered(player: Player): Promise<boolean> {
  const draw = player.page.getByTestId('draw');

  if (!(await draw.isEnabled().catch(() => false))) return false;

  await draw.click({ timeout: 5_000 }).catch(() => undefined);
  return true;
}

/**
 * Throws whichever tile is at the end of the hand, for a loop that only needs the hand to move on.
 *
 * Three steps, not two. The second tap on a tile no longer throws it - it puts it up, and the
 * dialog that opens is the one thing that sends it. Every step is allowed to come to nothing: a
 * loop like this runs against a table where the turn can pass between one line and the next.
 */
export async function throwAnyTile(player: Player): Promise<void> {
  const last = player.page.getByTestId('my-hand').locator('.tile-button').last();

  if (!(await last.isEnabled().catch(() => false))) return;

  await last.click({ timeout: 5_000 }).catch(() => undefined);
  await last.click({ timeout: 5_000 }).catch(() => undefined);

  // The dialog is the only thing that sends the tile, and it does not open at all if the two taps
  // landed either side of the turn ending - which is ordinary against a table that keeps playing.
  const confirm = player.page.getByTestId('discard-go');
  const asked = await confirm
    .waitFor({ state: 'visible', timeout: 2_000 })
    .then(() => true)
    .catch(() => false);

  if (asked) await confirm.click({ timeout: 5_000 }).catch(() => undefined);
}

/**
 * Says "not for me" to whatever discard is up, and says whether there was one.
 *
 * Every discard opens a window on every other seat now, and a window on a bot's discard has no
 * deadline at all - it waits for the people at the table. A spec driving one page has to answer
 * for that page or nothing moves.
 */
export async function passAnyClaim(player: Player): Promise<boolean> {
  const pass = player.page
    .getByTestId('claim-pass')
    .or(player.page.getByTestId('claim-pass-quick'))
    .first();

  if (!(await pass.isVisible().catch(() => false))) return false;

  await pass.click({ timeout: 5_000 }).catch(() => undefined);
  return true;
}

/** Waits until it is this player's turn to throw a tile, drawing and passing to get there. */
export async function waitForMyTurn(player: Player, timeout = 60_000): Promise<void> {
  const turnBar = player.page.getByTestId('turn-bar');
  const deadline = Date.now() + timeout;

  while (Date.now() < deadline) {
    if (await turnBar.isVisible().catch(() => false)) return;

    await passAnyClaim(player);
    await drawIfOffered(player);

    await turnBar.waitFor({ state: 'visible', timeout: 1_000 }).catch(() => undefined);
  }

  await expect(turnBar).toBeVisible({ timeout: 1_000 });
}
