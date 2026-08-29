import { describe, expect, it } from 'vitest';
import { TileView } from '../core/models';
import { HandBlock, TablePage, TurnMove } from './table';

/**
 * The route from "the server says you may kang" to a button the player can actually press.
 *
 * KANG-RULE.md was filed against this: four 8 bamboo in hand and no way to lay them down. The
 * server was right the whole time - the offer only exists once the seat has drawn - but nothing on
 * this side was covered at all, so there was no test that could have said so. These are the three
 * ways the offer reaches the screen: the block you built, the tile you lifted, and the Moves sheet.
 *
 * Called off the prototype, like the meld tests next door: these read `extraMoves` and their
 * arguments and nothing else, so there is no component to stand up and no table to connect to.
 */
type Internals = {
  blockMove(this: unknown, block: HandBlock): TurnMove | null;
  moveForFace(this: unknown, code: string): TurnMove | null;
  moveWord(this: unknown, move: TurnMove): string;
};

const proto = TablePage.prototype as unknown as Internals;

const FACE = 'B8';

/**
 * A seat that has drawn and holds four 8 bamboo: what the server sends once the turn is live.
 *
 * `moveForFace` is wired to the real one rather than stubbed, because it is half of what is being
 * tested here - `blockMove` is the block rule on top of it, and a stub would test neither.
 */
const offering = (...moves: TurnMove[]) => {
  const seat = {
    extraMoves: () => moves,
    moveForFace: (code: string) => proto.moveForFace.call(seat, code),
  };
  return seat;
};

const secretKang: TurnMove = {
  kind: 'SecretKang',
  face: FACE,
  label: 'Secret kang 8 bamboo',
  tiles: [FACE, FACE, FACE, FACE],
  testId: 'declare-secret-kang',
};

const sagasa: TurnMove = {
  kind: 'Sagasa',
  face: FACE,
  label: 'Sagasa 8 bamboo',
  tiles: [FACE, FACE, FACE, FACE],
  testId: 'declare-sagasa',
};

const tiles = (codes: string[]): TileView[] => codes.map((code, id) => ({ id, code }));

const block = (codes: string[]): HandBlock => ({
  key: 'k',
  kind: 'manual',
  tiles: tiles(codes),
  label: '',
  ariaLabel: '',
});

const four = [FACE, FACE, FACE, FACE];

describe('the secret kang button on a block of four', () => {
  it('is offered on four of the same face the server named', () => {
    const move = proto.blockMove.call(offering(secretKang), block(four));

    expect(move).toEqual(secretKang);
  });

  it('is not offered when the server named no face', () => {
    // Before you press Draw the turn carries no options at all, which is the whole of KANG-RULE.md.
    expect(proto.blockMove.call(offering(), block(four))).toBeNull();
  });

  it('is not offered on four tiles that are not all the same face', () => {
    expect(proto.blockMove.call(offering(secretKang), block([FACE, FACE, FACE, 'B7']))).toBeNull();
  });

  it('is not offered on three of the face', () => {
    expect(proto.blockMove.call(offering(secretKang), block([FACE, FACE, FACE]))).toBeNull();
  });

  it('is not offered on a block that holds the four plus a spare', () => {
    // A hand nobody grouped is one block of seventeen. The button belongs on the set, not the hand.
    expect(proto.blockMove.call(offering(secretKang), block([...four, 'B7']))).toBeNull();
  });

  it('is not offered for a sagasa, whose other three are already on the table', () => {
    expect(proto.blockMove.call(offering(sagasa), block(four))).toBeNull();
  });
});

describe('the secret kang button on the lifted tile', () => {
  it('finds the move by the face of the tile that is up', () => {
    expect(proto.moveForFace.call(offering(secretKang), FACE)).toEqual(secretKang);
  });

  it('finds nothing for a face the server did not name', () => {
    expect(proto.moveForFace.call(offering(secretKang), 'B7')).toBeNull();
  });

  it('finds nothing at all before the seat has drawn', () => {
    expect(proto.moveForFace.call(offering(), FACE)).toBeNull();
  });
});

describe('what the button says', () => {
  it('asks to show all four for a secret kang, because that is what declaring it does', () => {
    expect(proto.moveWord.call({}, secretKang)).toBe('Show all four');
  });

  it('names sagasa for a sagasa', () => {
    expect(proto.moveWord.call({}, sagasa)).toBe('Sagasa');
  });
});
