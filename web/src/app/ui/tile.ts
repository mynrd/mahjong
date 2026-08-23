import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TileCode } from '../core/models';

/**
 * Why a tile in your hand is outlined while somebody else's discard is up for claim.
 * `single` - exactly one way to use it. `multi` - more than one, so the player has to choose.
 * `win` - the discard finishes the hand, which outranks both.
 */
export type HintKind = 'none' | 'single' | 'multi' | 'win';

const SUIT_NAMES: Record<string, string> = {
  D: 'dots',
  B: 'bamboo',
  C: 'characters',
};

const WINDS = ['', 'east', 'south', 'west', 'north'];
const DRAGONS = ['', 'red dragon', 'green dragon', 'white dragon'];

/** Corner letters, in the same order as WINDS and DRAGONS above. */
const WIND_LETTERS = ['', 'E', 'S', 'W', 'N'];
const DRAGON_LETTERS = ['', 'R', 'F', 'P'];
const FLOWERS = ['', 'plum', 'orchid', 'chrysanthemum', 'bamboo flower'];
const SEASONS = ['', 'spring', 'summer', 'autumn', 'winter'];

/**
 * One mahjong tile.
 *
 * The artwork files are face art on a transparent background with no tile behind them, so the
 * cream body, the edge and the shadow are drawn here in CSS. That also means one place decides how
 * a tile looks, and the same body sits behind the eight flower and season faces drawn for this
 * project as behind the 34 that came from the tile set.
 */
@Component({
  selector: 'mj-tile',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div
      class="tile"
      [class.face-down]="faceDown()"
      [class.raised]="raised()"
      [class.dimmed]="dimmed()"
      [class.highlight]="highlight()"
      [class.picked]="picked()"
      [class.hint-single]="hint() === 'single'"
      [class.hint-multi]="hint() === 'multi'"
      [class.hint-win]="hint() === 'win'"
      [class.is-joker]="joker()"
      [attr.data-code]="code()"
      [attr.data-joker]="joker() ? 'yes' : null"
      [attr.data-hint]="hint() === 'none' ? null : hint()"
      [attr.data-picked]="picked() ? 'yes' : null"
      [attr.aria-label]="label()"
      role="img"
    >
      @if (!faceDown()) {
        <img [src]="'tiles/' + code() + '.svg'" [alt]="label()" draggable="false" />
      }

      <!-- The hint is never colour alone: PLAN.md section 7 rules that out, and a dashed gold
           outline is not something everyone can pick out of a solid gold one at tile size. -->
      @if (badge()) {
        <span class="badge" aria-hidden="true">{{ badge() }}</span>
      }

      <!-- The suit artwork alone makes a 4-dot and a 5-dot tile a counting exercise at hand size,
           so the rank sits in the corner of every numbered tile, and the winds and dragons carry
           their letter in the same spot. Flower and season art already has its number drawn in. -->
      @if (corner()) {
        <span class="corner" aria-hidden="true">{{ corner() }}</span>
      }

      <!-- A joker is playing as a face that is not the one printed on it, so the artwork alone is
           misleading: a 1-bam, an 8-dots and a 3-bam read as nonsense until the 8-dots is marked as
           the 2-bam it is standing in for. Bottom-left, clear of the rank and the claim badge. -->
      @if (joker()) {
        <span class="wild" aria-hidden="true">W</span>
      }
    </div>
  `,
  styles: `
    :host {
      display: inline-block;
      /* Everything scales off one variable, so a hand can be shrunk on a phone by changing the
         size in one place rather than restyling every part of the tile. */
      width: var(--tile-w, 46px);
      flex: 0 0 auto;
    }

    .tile {
      position: relative;
      width: 100%;
      aspect-ratio: 3 / 4;
      /* How much of the tile's width the face art fills. Everything left over is the cream margin
         the corner rank sits in, so this is the one number to change if the rank collides with the
         artwork or the artwork looks too small. */
      --tile-art: 78%;
      border-radius: calc(var(--tile-w, 46px) * 0.13);
      background: linear-gradient(#fffdf7, #f1e9d5);
      border: 1px solid #c9bb9b;
      box-shadow:
        0 calc(var(--tile-w, 46px) * 0.055) 0 #b3a37e,
        0 calc(var(--tile-w, 46px) * 0.09) calc(var(--tile-w, 46px) * 0.12) rgba(0, 0, 0, 0.35);
      overflow: hidden;
      transition:
        transform 120ms ease,
        box-shadow 120ms ease,
        filter 120ms ease;
    }

    /* The artwork fills its own 300x400 viewBox edge to edge, so at full bleed the rank lands on
       the top-left circle of a dots tile and D5 and D8 become a guess. Shrink the art and sit it on
       the bottom of the tile rather than re-cropping 34 SVGs from an external set. Height comes
       from the file's own 3:4 ratio, so the art is never squashed. */
    .tile img {
      position: absolute;
      left: 50%;
      bottom: calc(var(--tile-w, 46px) * 0.02);
      transform: translateX(-50%);
      display: block;
      width: var(--tile-art);
      user-select: none;
      -webkit-user-drag: none;
    }

    /* The white dragon is a frame drawn with a wide margin inside its own 300x400 viewBox, unlike
       the 33 faces that fill theirs edge to edge, so the shared --tile-art shrinks it twice and it
       reads as a much smaller tile. 92% is as far as it goes before the frame reaches the P. */
    .tile[data-code='R3'] {
      --tile-art: 92%;
    }

    /* A face-down tile is the same piece of bone as a face-up one, just turned over: same cream
       body, same edge, same shadow, no art. The only thing added is a darker border, because 13
       blank tiles in a row at 15px with a 1px gap otherwise read as one cream blob rather than a
       hand of thirteen. */
    .face-down {
      border-color: #b3a37e;
    }

    /* A tile lifted out of the hand, one tap before it is thrown. */
    .raised {
      transform: translateY(calc(var(--tile-w, 46px) * -0.28));
      box-shadow:
        0 calc(var(--tile-w, 46px) * 0.055) 0 #b3a37e,
        0 calc(var(--tile-w, 46px) * 0.22) calc(var(--tile-w, 46px) * 0.2) rgba(0, 0, 0, 0.45);
    }

    .highlight {
      outline: 3px solid #ffd35c;
      outline-offset: 1px;
    }

    .dimmed {
      filter: brightness(0.72) saturate(0.75);
    }

    /* ------------------------------------------------------------ claim hints
       Three states, each with its own outline style as well as its own colour, plus the corner
       badge above. Gold is already "this is live" on the discard pool and the active seat, so the
       one that means "you have to choose" is dashed to stay apart from it at a glance. */

    .hint-single {
      outline: 3px solid var(--ok, #4fbf7f);
      outline-offset: 1px;
    }

    .hint-multi {
      outline: 3px dashed var(--gold, #ffd35c);
      outline-offset: 2px;
    }

    .hint-win {
      outline: 3px solid var(--danger, #e05a4c);
      outline-offset: 1px;
      animation: pulse 1s ease-in-out infinite;
    }

    @keyframes pulse {
      50% {
        outline-offset: 4px;
      }
    }

    @media (prefers-reduced-motion: reduce) {
      .hint-win {
        animation: none;
      }
    }

    /* The tile the player has actually tapped. Lifted like a selected tile so the pick reads the
       same way a discard pick does, plus a ring that survives on top of any hint outline. */
    .picked {
      transform: translateY(calc(var(--tile-w, 46px) * -0.22));
      box-shadow:
        0 0 0 2px #fff inset,
        0 calc(var(--tile-w, 46px) * 0.055) 0 #b3a37e,
        0 calc(var(--tile-w, 46px) * 0.2) calc(var(--tile-w, 46px) * 0.18) rgba(0, 0, 0, 0.45);
    }

    /* Top-left, so it never sits under the claim badge in the top-right corner. */
    .corner {
      position: absolute;
      top: calc(var(--tile-w, 46px) * 0.03);
      left: calc(var(--tile-w, 46px) * 0.08);
      font-size: calc(var(--tile-w, 46px) * 0.24);
      font-weight: 700;
      line-height: 1;
      letter-spacing: -0.03em;
      color: #6d6152;
      pointer-events: none;
    }

    /* Matched to the ink of each tile's artwork so the mark reads as part of the tile. Winds and
       the white dragon are drawn in blue, the red dragon in red, the green dragon in green. */
    .tile[data-code^='D'] .corner,
    .tile[data-code^='W'] .corner,
    .tile[data-code='R3'] .corner {
      color: #1d4f8c;
    }

    .tile[data-code^='B'] .corner,
    .tile[data-code='R2'] .corner {
      color: #1d7a49;
    }

    .tile[data-code^='C'] .corner,
    .tile[data-code='R1'] .corner {
      color: #a83228;
    }

    /* Not colour alone, same rule as the hints: the corner letter is what carries it if the
       violet outline does not. */
    .is-joker {
      outline: 2px solid var(--wild, #a97bff);
      outline-offset: 1px;
    }

    .wild {
      position: absolute;
      bottom: calc(var(--tile-w, 46px) * 0.03);
      left: calc(var(--tile-w, 46px) * 0.08);
      font-size: calc(var(--tile-w, 46px) * 0.24);
      font-weight: 800;
      line-height: 1;
      color: var(--wild, #a97bff);
    }

    .badge {
      position: absolute;
      top: 1px;
      right: 1px;
      min-width: calc(var(--tile-w, 46px) * 0.34);
      padding: 0 calc(var(--tile-w, 46px) * 0.04);
      font-size: calc(var(--tile-w, 46px) * 0.28);
      font-weight: 800;
      line-height: 1.35;
      text-align: center;
      color: #16281f;
      background: #fff;
      border-radius: calc(var(--tile-w, 46px) * 0.09);
      box-shadow: 0 0 0 1px rgba(0, 0, 0, 0.35);
    }
  `,
})
export class Tile {
  /** Tile code such as "D5", or "back" for a tile shown face down. */
  readonly code = input.required<TileCode>();

  readonly raised = input(false);
  readonly dimmed = input(false);
  readonly highlight = input(false);

  /** How this tile could be used on the discard that is currently up for claim. */
  readonly hint = input<HintKind>('none');

  /** One of the tiles the player has tapped to build a claim with. */
  readonly picked = input(false);

  /**
   * Wild this hand. Drawn with a mark of its own, because the face printed on the tile is not the
   * face it is playing as, and an unmarked joker makes a legal set look like a broken one.
   */
  readonly joker = input(false);

  /**
   * The letter in the corner of a hinted tile. The outline colour alone would not carry the
   * difference to anyone who cannot separate the two, and at 34px the dashes are easy to miss.
   */
  readonly badgeText = input<string | null>(null);

  protected readonly faceDown = computed(() => this.code() === 'back');

  protected readonly badge = computed(() => (this.hint() === 'none' ? null : this.badgeText()));

  /**
   * The mark in the top-left corner: "1".."9" on a dots, bamboo or characters tile, E/S/W/N on a
   * wind, R/F/P on the red, green and white dragon. Flowers and seasons get nothing here - their
   * artwork is drawn with the number already in the corner.
   */
  protected readonly corner = computed(() => {
    const code = this.code();
    if (this.faceDown()) return null;

    const suit = code[0];
    const rank = Number(code.slice(1));

    if (suit in SUIT_NAMES) return code.slice(1);
    if (suit === 'W') return WIND_LETTERS[rank] ?? null;
    if (suit === 'R') return DRAGON_LETTERS[rank] ?? null;

    return null;
  });

  /** Spoken description, so a hand is readable without seeing the artwork. */
  protected readonly label = computed(() =>
    this.joker() ? `${describe(this.code())}, joker` : describe(this.code()),
  );
}

export function describe(code: TileCode): string {
  if (code === 'back') return 'face down tile';

  const suit = code[0];
  const rank = Number(code.slice(1));

  if (suit in SUIT_NAMES) return `${rank} ${SUIT_NAMES[suit]}`;
  if (suit === 'W') return `${WINDS[rank]} wind`;
  if (suit === 'R') return DRAGONS[rank];
  if (suit === 'F') return `${FLOWERS[rank]} flower`;
  if (suit === 'S') return `${SEASONS[rank]} season`;

  return code;
}
