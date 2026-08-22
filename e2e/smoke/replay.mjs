// Plays a hand to the end, then reads it back through the replay API.
//
// This is the half of the replay feature that unit tests cannot reach: whether a frame is actually
// written per move, whether the password gate holds, and whether the frames that come back describe
// the hand that was played. It checks the two things that would be quietly wrong rather than loud:
// a replay that shows only some seats, and a replay reachable without the password.
//
//   node smoke/replay.mjs [apiBaseUrl]

import * as signalR from '@microsoft/signalr';

const API = process.argv[2] ?? 'http://localhost:5080';
const PASSWORD = 'mahjong1';

const log = (...args) => console.log(...args);
const fail = (message) => {
  console.error(`FAIL: ${message}`);
  process.exit(1);
};

let checks = 0;
const check = (condition, message) => {
  checks++;
  if (!condition) fail(message);
};

async function api(path, { method = 'GET', body, token, expect } = {}) {
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

  if (expect !== undefined) {
    check(response.status === expect, `${method} ${path} -> ${response.status}, expected ${expect}: ${text}`);
    return payload;
  }

  if (!response.ok) throw new Error(`${method} ${path} -> ${response.status} ${text}`);
  return payload;
}

// --------------------------------------------------------------------- play one hand

async function playAHand(room) {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl(`${API}/hubs/game?access_token=${encodeURIComponent(room.playerToken)}`)
    .configureLogging(signalR.LogLevel.Error)
    .build();

  let finished = null;
  let latest = null;
  let busy = false;

  connection.on('StateChanged', (view) => {
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
      // Claiming rather than passing is what gets a hand finished, and it is also what puts melds
      // into the frames, which is a shape the replay has to render.
      const kind = ['Todas', 'Kang', 'Pung', 'Chow'].find((k) => view.claim.yourOptions.includes(k));

      if (!kind) await connection.invoke('Pass');
      else await connection.invoke('Claim', kind, kind === 'Chow' ? chowPartners(view) : []);
      return;
    }

    if (view.currentSeat !== view.yourSeat) return;

    if (view.phase === 'AwaitingDraw') {
      await connection.invoke('Draw');
      return;
    }

    if (view.phase === 'AwaitingDiscard') {
      if (view.yourTurn?.canDeclareTodas) {
        await connection.invoke('DeclareTodas');
        return;
      }

      await connection.invoke('Discard', view.seats[view.yourSeat].concealed[0].id);
    }
  }

  await connection.start();
  await api(`/api/rooms/${room.roomCode}/start`, { method: 'POST', body: {}, token: room.playerToken });

  const deadline = Date.now() + 180_000;
  while (!finished && Date.now() < deadline) await new Promise((r) => setTimeout(r, 200));

  await connection.stop();

  if (!finished) fail('the hand did not finish within 180s');
  return finished;
}

/** The two held tiles that make a run with the claimed tile. Same shape as play-hand.mjs. */
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

// --------------------------------------------------------------------- run

// A one-second claim window. The default six is right for people and turns a hand into six minutes
// of a script sitting still, since every discard nobody wants runs the window to its end.
const RULES = { claimWindowSeconds: 1 };

const room = await api('/api/rooms', {
  method: 'POST',
  body: { name: 'Replay Smoke', password: PASSWORD, displayName: 'Human', rules: RULES },
});

await api(`/api/rooms/${room.roomCode}/bots`, { method: 'POST', body: {}, token: room.playerToken });
log(`room ${room.roomCode} created`);

const finished = await playAHand(room);
log(`hand 1 finished: ${finished.outcome.reason}, winner seat ${finished.outcome.winnerSeat ?? 'none'}`);

// ------------------------------------------------------------------ the password gate

await api(`/api/rooms/${room.roomCode}/replays`, { expect: 401 });
await api(`/api/rooms/${room.roomCode}/replays`, { token: 'not-a-real-token', expect: 401 });
await api(`/api/rooms/${room.roomCode}/replays/1`, { expect: 401 });

await api(`/api/rooms/${room.roomCode}/replay/unlock`, {
  method: 'POST',
  body: { password: 'wrong-password' },
  expect: 401,
});

// A seat token is not a replay token. Holding a seat does not skip the password: the two grants are
// deliberately separate, and this is the line that proves they did not get merged.
await api(`/api/rooms/${room.roomCode}/replays`, { token: room.playerToken, expect: 401 });

const { token } = await api(`/api/rooms/${room.roomCode}/replay/unlock`, {
  method: 'POST',
  body: { password: PASSWORD },
});

check(typeof token === 'string' && token.length > 20, 'unlock did not return a usable token');
log('password gate holds');

// A token for one room must not open another.
const other = await api('/api/rooms', {
  method: 'POST',
  body: { name: 'Another Table', password: 'different1', displayName: 'Nobody', rules: RULES },
});

await api(`/api/rooms/${other.roomCode}/replays`, { token, expect: 401 });
log('a token for one table does not open another');

// ------------------------------------------------------------------ the list

const hands = await api(`/api/rooms/${room.roomCode}/replays`, { token });

check(hands.length === 1, `expected 1 finished hand, got ${hands.length}`);
check(hands[0].handNumber === 1, `expected hand 1, got ${hands[0].handNumber}`);
check(hands[0].endedAt != null, 'the finished hand has no endedAt');
check(hands[0].frameCount > 0, 'no frames were recorded for the hand');
check(hands[0].reason === finished.outcome.reason, `list says ${hands[0].reason}, the hand said ${finished.outcome.reason}`);

if (finished.outcome.winnerSeat !== null && finished.outcome.winnerSeat !== undefined) {
  check(hands[0].winnerSeat === finished.outcome.winnerSeat, 'the list names a different winner');
  check(typeof hands[0].winnerName === 'string', 'the winning seat has no name in the list');
}

log(`list: hand 1, ${hands[0].frameCount} frames, ${hands[0].reason}`);

// A hand that has not finished must not be listed or readable.
await api(`/api/rooms/${room.roomCode}/start`, { method: 'POST', body: {}, token: room.playerToken });

const midHand = await api(`/api/rooms/${room.roomCode}/replays`, { token });
check(midHand.length === 1, `a hand in progress showed up in the list (${midHand.length} entries)`);
await api(`/api/rooms/${room.roomCode}/replays/2`, { token, expect: 409 });
log('a hand in progress is neither listed nor readable');

// ------------------------------------------------------------------ the frames

const replay = await api(`/api/rooms/${room.roomCode}/replays/1`, { token });

check(replay.frames.length === hands[0].frameCount, 'the list and the replay disagree on the frame count');
check(replay.handNumber === 1, 'the replay is for the wrong hand');

const raw = JSON.stringify(replay);
check(!raw.includes('"wall"'), 'the wall was sent to a replay client');
check(!raw.includes('"frontIndex"'), 'a wall index was sent to a replay client');

let previousDiscards = -1;

for (const frame of replay.frames) {
  check(frame.seats.length === 4, `frame ${frame.index} has ${frame.seats.length} seats`);
  check(typeof frame.caption === 'string' && frame.caption.length > 0, `frame ${frame.index} has no caption`);

  for (const seat of frame.seats) {
    check(Array.isArray(seat.concealed), `frame ${frame.index} seat ${seat.seat} has no concealed tiles array`);

    // The point of the whole feature: every seat face up, not just one.
    if (frame.index === 0)
      check(seat.concealed.length >= 16, `frame 0 seat ${seat.seat} holds only ${seat.concealed.length} tiles`);

    check(
      !seat.concealed.some((t) => /^[FS]/.test(t.code)),
      `frame ${frame.index} seat ${seat.seat} has a bonus tile sitting in hand`,
    );

    const melded = seat.melds.reduce((n, m) => n + (m.kind === 'Kang' ? 3 : m.tiles.length), 0);
    const total = seat.concealed.length + melded;

    // Null fields are dropped from the payload, so an unfinished frame has no `outcome` key at all
    // rather than an explicit null. Truthiness is the check that works for both shapes.
    if (!frame.outcome)
      check(total === 16 || total === 17, `frame ${frame.index} seat ${seat.seat} holds ${total} tiles`);
  }

  // Discards only ever grow within a hand, so a frame list that is out of order shows up here.
  check(
    frame.discards.length >= previousDiscards,
    `frame ${frame.index} has fewer discards (${frame.discards.length}) than the frame before it (${previousDiscards})`,
  );
  previousDiscards = frame.discards.length;
}

const first = replay.frames[0];
const last = replay.frames[replay.frames.length - 1];

check(!first.outcome, 'the opening frame already has an outcome');
check(!!last.outcome, 'the closing frame has no outcome');
check(last.outcome.reason === finished.outcome.reason, 'the closing frame disagrees with the hand about how it ended');
check(last.outcome.totalUnits === finished.outcome.totalUnits, 'the closing frame disagrees about the units');

// Captions come out of the action log payloads. Those were stored as {"seat":N} and nothing else
// until GameJson.SerializeEvent, which does not throw - it just says the wrong tile - so the check
// is that the captions actually name tiles rather than that they parse.
const named = replay.frames.filter((f) => /\b(dots|bamboo|characters|wind|dragon|flower|season)\b/.test(f.caption));
check(named.length > 3, `only ${named.length} frames name a tile, so the action log payloads are empty again`);

const fallbacks = replay.frames.filter((f) => /^[^:]+: [A-Z]\w+$/.test(f.caption));
check(fallbacks.length === 0, `${fallbacks.length} frames fell back to a raw event name, e.g. "${fallbacks[0]?.caption}"`);

log(`frames: ${replay.frames.length}, first "${first.caption}", last "${last.caption}"`);
log('');
log(`OK - ${checks} checks passed`);
