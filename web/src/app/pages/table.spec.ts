import { describe, expect, it } from 'vitest';
import { ClaimCandidateView, ClaimKind, MeldView, TileView } from '../core/models';
import { TablePage, barCallsFor } from './table';

/**
 * Which four-tile groups the table draws face down.
 *
 * The server sends one flag, `concealed`, and the tiles behind it. Everything about whether a
 * player's hand stays their own once it is on the table comes down to what this one function does
 * with that flag: a secret kang has to come out as four backs for the three seats that are not
 * holding it, and every other group - including the two kangs that were public before they were
 * four tiles - has to come out as itself.
 *
 * Called off the prototype because it reads its two arguments and nothing else, so there is no
 * component to stand up and no table to connect to.
 */
const meldFaces = (
  TablePage.prototype as unknown as {
    meldFaces(meld: MeldView, isMine: boolean): string[];
  }
).meldFaces;

const FACE = 'C5';

const tiles = (count: number, code = FACE): TileView[] =>
  Array.from({ length: count }, (_, id) => ({ id, code }));

/** Four copies a player drew themselves and declared. Nobody else has seen any of them. */
const secretKang: MeldView = {
  kind: 'Kang',
  tiles: tiles(4),
  concealed: true,
  claimedFromSeat: null,
  fromSagasa: false,
};

/** Three in hand, the fourth taken off seat 0's discard. */
const openKang: MeldView = {
  kind: 'Kang',
  tiles: tiles(4),
  concealed: false,
  claimedFromSeat: 0,
  fromSagasa: false,
};

/** A pung that was already on the table, grown by the fourth copy turning up in your own draw. */
const sagasaKang: MeldView = {
  kind: 'Kang',
  tiles: tiles(4),
  concealed: false,
  claimedFromSeat: 1,
  fromSagasa: true,
};

const pung: MeldView = {
  kind: 'Pung',
  tiles: tiles(3),
  concealed: false,
  claimedFromSeat: 1,
  fromSagasa: false,
};

const backs = (count: number) => Array.from({ length: count }, () => 'back');

describe('meldFaces', () => {
  it('draws a secret kang as four backs for the other three seats', () => {
    expect(meldFaces(secretKang, false)).toEqual(backs(4));
  });

  it('draws a secret kang face up for the player who declared it', () => {
    expect(meldFaces(secretKang, true)).toEqual([FACE, FACE, FACE, FACE]);
  });

  it('still shows the other seats that four tiles are there', () => {
    // The call is public even though the tiles are not: everybody saw the ambition get paid and
    // the replacement tile drawn, so hiding the count as well would only confuse the table.
    expect(meldFaces(secretKang, false)).toHaveLength(4);
  });

  it('draws a kang taken off a discard face up to everybody', () => {
    expect(meldFaces(openKang, false)).toEqual([FACE, FACE, FACE, FACE]);
    expect(meldFaces(openKang, true)).toEqual([FACE, FACE, FACE, FACE]);
  });

  it('draws a sagasa kang face up to everybody', () => {
    // The fourth tile was drawn, not claimed, but the pung under it was on the table already.
    expect(meldFaces(sagasaKang, false)).toEqual([FACE, FACE, FACE, FACE]);
    expect(meldFaces(sagasaKang, true)).toEqual([FACE, FACE, FACE, FACE]);
  });

  it('draws an ordinary claimed set face up to everybody', () => {
    expect(meldFaces(pung, false)).toEqual([FACE, FACE, FACE]);
  });

  it('hides on the concealed flag alone, not on the kind of set', () => {
    // If this ever starts keying off `kind` or `fromSagasa`, one of the two public kangs will end
    // up hidden or the secret one shown.
    expect(meldFaces({ ...openKang, concealed: true }, false)).toEqual(backs(4));
    expect(meldFaces({ ...secretKang, concealed: false }, false)).toEqual([FACE, FACE, FACE, FACE]);
  });

  it('keeps the real tiles out of the array it hands the other seats', () => {
    // The faces do reach their browser - the server sends them - so this array is the only thing
    // standing between an opponent and the tile. Nothing in it may carry the code.
    expect(meldFaces(secretKang, false)).not.toContain(FACE);
  });
});

/**
 * Which calls the action bar offers on an open discard.
 *
 * The bar is now where a discard is answered - no sheet has to be opened to pung a tile - so this
 * one function decides what a player sees at the moment a tile hits the table and how many taps it
 * costs them. Two things it has to get right: a call with one shape behind it must be pressable
 * outright, and a call already beaten must still be there, dead, rather than disappearing out from
 * under a thumb.
 */
describe('barCallsFor', () => {
  const candidate = (kind: ClaimKind, tileIds: number[]): ClaimCandidateView => ({
    kind,
    tileIds,
    describe: `${kind} something`,
  });

  const allLive = () => true;

  it('offers one button per kind, however many shapes are behind it', () => {
    // Two chows off the same tile is one Chow button carrying a count, not two Chow buttons: the
    // bar has no room to draw the difference, and the dialog does.
    const calls = barCallsFor(
      [candidate('Chow', [1, 2]), candidate('Chow', [2, 3]), candidate('Pung', [4, 5])],
      [],
      true,
      allLive,
    );

    expect(calls.map((c) => c.kind)).toEqual(['Pung', 'Chow']);
    expect(calls.find((c) => c.kind === 'Chow')?.options).toBe(2);
    expect(calls.find((c) => c.kind === 'Pung')?.options).toBe(1);
  });

  it('puts the calls in the order they outrank each other', () => {
    const calls = barCallsFor(
      [candidate('Chow', [1, 2]), candidate('Todas', []), candidate('Pung', [3, 4])],
      [],
      true,
      allLive,
    );

    expect(calls.map((c) => c.kind)).toEqual(['Todas', 'Pung', 'Chow']);
  });

  it('offers the bare calls when nothing has been read for the player', () => {
    // Assist off: the server says nothing about what the tile is worth, so every call the rules
    // could allow is offered and none of them carries a count - pressing one is only half an
    // answer there.
    const calls = barCallsFor([], ['Chow', 'Pung', 'Kang', 'Todas'], false, allLive);

    expect(calls.map((c) => c.kind)).toEqual(['Chow', 'Pung', 'Kang', 'Todas']);
    expect(calls.every((c) => c.options === 0)).toBe(true);
  });

  it('ignores the candidate list entirely when the helper is off', () => {
    // Nothing worked out for this player may leak onto their bar through the back door: an
    // unassisted table sends no candidates, and a build that read them anyway would quietly start
    // helping at a table that switched the helping off.
    const calls = barCallsFor([candidate('Pung', [1, 2])], ['Kang'], false, allLive);

    expect(calls.map((c) => c.kind)).toEqual(['Kang']);
  });

  it('keeps a beaten call on the bar, marked dead', () => {
    // A button that vanished mid-window is a button the thumb was already moving towards. It stays
    // where it was and stops working instead.
    const calls = barCallsFor(
      [candidate('Chow', [1, 2]), candidate('Pung', [3, 4])],
      [],
      true,
      (kind) => kind !== 'Chow',
    );

    expect(calls).toHaveLength(2);
    expect(calls.find((c) => c.kind === 'Chow')?.live).toBe(false);
    expect(calls.find((c) => c.kind === 'Pung')?.live).toBe(true);
  });

  it('offers nothing when the tile takes nothing', () => {
    expect(barCallsFor([], [], true, allLive)).toEqual([]);
  });
});

/** The word on a bar call. Short, because four of them share one row on a phone. */
describe('barCallWord', () => {
  const barCallWord = (
    TablePage.prototype as unknown as {
      barCallWord(call: { kind: ClaimKind; options: number }): string;
    }
  ).barCallWord;

  it('names the call and nothing else when there is only one way to make it', () => {
    expect(barCallWord({ kind: 'Pung', options: 1 })).toBe('Pung');
    expect(barCallWord({ kind: 'Chow', options: 0 })).toBe('Chow');
  });

  it('counts the shapes only when there is a choice to be made', () => {
    // The count is the warning that pressing this opens the dialog rather than taking the tile.
    expect(barCallWord({ kind: 'Chow', options: 3 })).toBe('Chow (3)');
  });

  it('keeps the shout on a todas', () => {
    expect(barCallWord({ kind: 'Todas', options: 1 })).toBe('Todas!');
  });
});
