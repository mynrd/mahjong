// Drives one whole hand against the real API, with no browser involved.
//
// This exists to separate "the server is wrong" from "the UI is wrong" later on. It creates a
// room, sits three bots down, joins as the human host and then plays automatically, taking a win
// when one is offered and otherwise discarding. It checks the invariants that matter along the
// way: every seat holds 16 tiles between turns, nobody is ever sent another seat's tiles, and the
// hand reaches a real ending.
//
//   node smoke/play-hand.mjs [apiBaseUrl]

import * as signalR from '@microsoft/signalr';

const API = process.argv[2] ?? 'http://localhost:5080';
const PASSWORD = 'mahjong1';

const log = (...args) => console.log(...args);
const fail = (message) => {
  console.error(`FAIL: ${message}`);
  process.exit(1);
};

async function api(path, { method = 'GET', body, token } = {}) {
  const response = await fetch(`${API}${path}`, {
    method,
    headers: {
      'content-type': 'application/json',
      ...(token ? { authorization: `Bearer ${token}` } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });

  const text = await response.text();
  const payload = text ? JSON.parse(text) : null;

  if (!response.ok) throw new Error(`${method} ${path} -> ${response.status} ${text}`);
  return payload;
}

// --------------------------------------------------------------------- invariants

let checks = 0;

function checkView(view) {
  checks++;

  const me = view.seats[view.yourSeat];
  if (!me.concealed) fail(`seat ${view.yourSeat} was not sent its own tiles`);

  for (const seat of view.seats) {
    if (seat.seat !== view.yourSeat && seat.concealed !== null && seat.concealed !== undefined)
      fail(`seat ${view.yourSeat} was sent seat ${seat.seat}'s concealed tiles`);
  }

  if ('wall' in view) fail('the wall was sent to a client');

  // Between turns everyone holds 16; the seat to act holds 17. Melds count a kang as three,
  // because the replacement draw pays for the fourth tile.
  for (const seat of view.seats) {
    const melded = seat.melds.reduce((n, m) => n + (m.kind === 'Kang' ? 3 : m.tiles.length), 0);
    const total = seat.concealedCount + melded;
    const expected = seat.seat === view.currentSeat && view.phase === 'AwaitingDiscard' ? 17 : 16;

    if (view.phase !== 'HandOver' && total !== expected)
      fail(`seat ${seat.seat} holds ${total} tiles, expected ${expected} (phase ${view.phase})`);

    if (seat.concealed?.some((t) => t.code.match(/^[WRFS]/)))
      fail(`seat ${seat.seat} has a bonus tile sitting in hand`);
  }
}

// --------------------------------------------------------------------- run

const room = await api('/api/rooms', {
  method: 'POST',
  body: { name: 'Smoke Test', password: PASSWORD, displayName: 'Human' },
});

log(`room ${room.roomCode} created, host on seat ${room.seat}`);

await api(`/api/rooms/${room.roomCode}/bots`, {
  method: 'POST',
  body: {},
  token: room.playerToken,
});

const lobby = await api(`/api/rooms/${room.roomCode}`);
log(`seats: ${lobby.seats.map((s) => `${s.seat}=${s.displayName}${s.isBot ? ' (bot)' : ''}`).join(', ')}`);
if (!lobby.canStart) fail('room did not fill up');

const connection = new signalR.HubConnectionBuilder()
  .withUrl(`${API}/hubs/game?access_token=${encodeURIComponent(room.playerToken)}`)
  .configureLogging(signalR.LogLevel.Error)
  .build();

let finished = null;
let moves = 0;

// Updates arrive faster than we can answer them: three bots move in quick succession while we are
// still awaiting an invoke. Dropping the ones that land while busy loses the only update that
// mattered - the one saying it is our turn again - and the hand hangs forever. So the newest view
// is kept and re-examined once the current action finishes. Latest wins, nothing is dropped.
let latest = null;
let busy = false;

connection.on('StateChanged', (view) => {
  checkView(view);
  latest = view;
  void pump();
});

async function pump() {
  if (busy) return;
  busy = true;

  try {
    while (latest) {
      const view = latest;
      latest = null;
      await act(view);
    }
  } finally {
    busy = false;
  }
}

async function act(view) {
  if (view.phase === 'HandOver') {
    finished ??= view;
    return;
  }

  if (view.claim && !view.claim.youAnswered) {
    // Prefer the strongest claim on offer. Claiming rather than passing is what actually gets a
    // hand finished: with four players discarding at random, a 17-tile hand almost never completes
    // on self-draw alone before the wall runs out.
    const order = ['Todas', 'Kang', 'Pung', 'Chow'];
    const kind = order.find((k) => view.claim.yourOptions.includes(k));

    if (!kind) {
      await connection.invoke('Pass');
      return;
    }

    await connection.invoke('Claim', kind, kind === 'Chow' ? chowPartners(view) : []);
    return;
  }

  if (view.currentSeat !== view.yourSeat) return;

  if (view.phase === 'AwaitingDraw') {
    await connection.invoke('Draw');
    return;
  }

  if (view.phase === 'AwaitingDiscard') {
    if (view.yourTurn?.canDeclareTodas) {
      log('declaring todas');
      await connection.invoke('DeclareTodas');
      return;
    }

    moves++;
    await connection.invoke('Discard', leastUseful(view).id);
  }
}

/** The two held tiles that make a run with the claimed tile. */
function chowPartners(view) {
  const tile = view.claim.tile;
  const suit = tile.code[0];
  const rank = Number(tile.code[1]);
  const mine = view.seats[view.yourSeat].concealed;

  const held = (r) => mine.find((t) => t.code === `${suit}${r}`);

  for (const low of [rank - 2, rank - 1, rank]) {
    if (low < 1 || low + 2 > 9) continue;
    const wanted = [low, low + 1, low + 2].filter((r) => r !== rank);
    const a = held(wanted[0]);
    const b = held(wanted[1]);
    if (a && b) return [a.id, b.id];
  }

  return [];
}

/**
 * Scores every tile by how much it is doing for the hand - copies of itself, plus near neighbours
 * in the same suit that could still form a run - and throws the lowest. Same idea as the server
 * side bot, so the human seat is not simply feeding the table.
 */
function leastUseful(view) {
  const mine = view.seats[view.yourSeat].concealed;
  const counts = new Map();
  for (const tile of mine) counts.set(tile.code, (counts.get(tile.code) ?? 0) + 1);

  const score = (tile) => {
    const suit = tile.code[0];
    const rank = Number(tile.code[1]);
    let total = (counts.get(tile.code) ?? 0) * 3;

    for (const offset of [1, 2]) {
      total += (counts.get(`${suit}${rank - offset}`) ?? 0) * (3 - offset);
      total += (counts.get(`${suit}${rank + offset}`) ?? 0) * (3 - offset);
    }

    if (rank === 1 || rank === 9) total -= 1;
    return total;
  };

  return mine.reduce((worst, tile) => (score(tile) < score(worst) ? tile : worst), mine[0]);
}

await connection.start();
log('connected to the hub');

const HANDS = Number(process.env.HANDS ?? 6);
let wins = 0;
let draws = 0;

for (let hand = 1; hand <= HANDS; hand++) {
  finished = null;
  latest = null;

  await api(`/api/rooms/${room.roomCode}/start`, { method: 'POST', body: {}, token: room.playerToken });

  const deadline = Date.now() + 120_000;
  while (!finished && Date.now() < deadline) await new Promise((r) => setTimeout(r, 200));

  if (!finished) fail(`hand ${hand} did not finish within 120s (${checks} views checked)`);

  const outcome = finished.outcome;
  const sum = outcome.settlements.reduce((n, s) => n + s.delta, 0);
  if (sum !== 0) fail(`hand ${hand} settlements sum to ${sum}, not 0`);

  if (outcome.winnerSeat === null || outcome.winnerSeat === undefined) {
    draws++;
    log(`hand ${hand}: ${outcome.reason}, nobody paid`);
    continue;
  }

  wins++;
  const name = finished.seats[outcome.winnerSeat].displayName;
  const bonuses = outcome.breakdown.map((b) => `${b.name} ${b.units}`).join(', ');
  const money = outcome.settlements.map((s) => `s${s.seat} ${s.delta > 0 ? '+' : ''}${s.delta}`).join('  ');

  log(`hand ${hand}: ${outcome.reason} by seat ${outcome.winnerSeat} (${name}) for ${outcome.totalUnits} units`);
  log(`         ${bonuses}`);
  log(`         ${money}`);
}

await connection.stop();

log('');
const balances = await api(`/api/rooms/${room.roomCode}`);
log(`running totals: ${balances.seats.map((s) => `${s.displayName} ${s.balance > 0 ? '+' : ''}${s.balance}`).join('  ')}`);

const table = balances.seats.reduce((n, s) => n + s.balance, 0);
if (table !== 0) fail(`the table's balances sum to ${table}, not 0 - money was created or destroyed`);

if (wins === 0) fail(`no hand was won in ${HANDS} attempts, so scoring was never exercised`);

log('');
log(`OK - ${HANDS} hands (${wins} won, ${draws} drawn), ${checks} views checked, every settlement balanced`);
