/** Wire types. These mirror the API contracts in Mahjong.Api exactly. */

export type SetKind = 'Pair' | 'Chow' | 'Pung' | 'Kang';
export type ClaimKind = 'Chow' | 'Pung' | 'Kang' | 'Todas';
export type GamePhase = 'AwaitingDraw' | 'AwaitingDiscard' | 'AwaitingClaims' | 'HandOver';
export type HandEndReason = 'Todas' | 'WallExhausted' | 'Bisaklat';
export type RoomStatus = 'Lobby' | 'Playing' | 'Closed';

/** A tile face code: D/B/C for the numbered suits, W/R for winds and dragons, F/S for bonus. */
export type TileCode = string;

export interface TileView {
  id: number;
  code: TileCode;
}

export interface MeldView {
  kind: SetKind;
  tiles: TileView[];
  concealed: boolean;
  claimedFromSeat: number | null;
  fromSagasa: boolean;
}

export type HandGroupKind = 'Kang' | 'Pung' | 'Chow' | 'Pair' | 'Partial' | 'Floater';

/** One block of your own hand, as Auto Arrange lays it out. */
export interface HandGroupView {
  kind: HandGroupKind;
  tiles: TileView[];
  /** Face codes that would complete the group. Empty for a complete set, a pair or a floater. */
  needs: TileCode[];
  jokersUsed: number;
}

export interface SeatStateView {
  seat: number;
  displayName: string | null;
  isBot: boolean;
  isConnected: boolean;
  concealedCount: number;
  /** Filled in only for your own seat. Null for everyone else, by design. */
  concealed: TileView[] | null;
  /** Also your own seat only: it is read off `concealed`, so it gives away the same thing. */
  groups: HandGroupView[] | null;
  melds: MeldView[];
  bonus: TileView[];
  balance: number;
}

export interface DiscardView {
  seat: number;
  tile: TileView;
  claimed: boolean;
}

/** One concrete way you could take the discard, with the exact tiles from your hand it costs. */
export interface ClaimCandidateView {
  kind: ClaimKind;
  /** Ids from your own hand, not including the discard. Empty for a Todas. */
  tileIds: number[];
  /** Label for the button, e.g. "Chow B3-B4-B5". Built server side so it cannot drift. */
  describe: string;
}

export interface ClaimPromptView {
  tile: TileView;
  fromSeat: number;
  /** Null at an unassisted table, where the window has no deadline. */
  deadlineUtc: string | null;
  /** How long the window was when it opened. The countdown bar needs this to know how full to be. */
  windowSeconds: number;
  /** Empty at an unassisted table: working out what you can take is your job there. */
  yourOptions: ClaimKind[];
  /** One entry per distinct legal group. Highest-ranked kind first. Empty when unassisted. */
  candidates: ClaimCandidateView[];
  youAnswered: boolean;
  /** Unassisted: what you pressed and still owe the tiles for. */
  pressedKind: ClaimKind | null;
  /** When that press is dropped for taking too long. Set for a pung or kang, null for a chow. */
  namingDeadlineUtc: string | null;
  /** How long that clock was when it started, for the countdown bar. */
  namingSeconds: number;
  /** You pressed pung or kang and never named the tiles in time. Out of this discard. */
  burned: boolean;
  /** You have a claim on this tile, finished or half made. Unlike youAnswered, a pass is not one. */
  youClaimed: boolean;
}

export interface TurnOptionsView {
  canDiscard: boolean;
  canDeclareTodas: boolean;
  secretKangFaces: TileCode[];
  sagasaFaces: TileCode[];
}

export interface ScoreLineView {
  name: string;
  units: number;
}

export interface SettlementView {
  seat: number;
  delta: number;
  reason: string;
}

export interface OutcomeView {
  reason: HandEndReason;
  winnerSeat: number | null;
  totalUnits: number;
  breakdown: ScoreLineView[];
  settlements: SettlementView[];
}

export interface PlayerGameView {
  roomCode: string;
  handNumber: number;
  yourSeat: number;
  manoSeat: number;
  currentSeat: number;
  phase: GamePhase;
  joker: TileCode | null;
  tilesRemaining: number;
  seats: SeatStateView[];
  discards: DiscardView[];
  claim: ClaimPromptView | null;
  yourTurn: TurnOptionsView | null;
  outcome: OutcomeView | null;
  /**
   * Whether this table lets the server help. Off, no claim is spelled out and no hand is laid out
   * for you: the claim strip is four bare buttons and Auto Arrange is gone.
   */
  assisted: boolean;
}

export interface SeatView {
  seat: number;
  displayName: string | null;
  isBot: boolean;
  isConnected: boolean;
  isHost: boolean;
  balance: number;
}

export interface RoomView {
  code: string;
  name: string;
  status: RoomStatus;
  inviteUrl: string;
  handsPlayed: number;
  seats: SeatView[];
  takenSeats: number;
  canStart: boolean;
}

export interface SeatedResponse {
  roomCode: string;
  inviteUrl: string;
  playerId: string;
  seat: number;
  playerToken: string;
  isHost: boolean;
}

export interface WhoAmIResponse {
  playerId: string;
  seat: number;
  displayName: string;
  isHost: boolean;
  room: RoomView;
}

/** One seat in a replay. Unlike SeatStateView, every tile is face up: that is the point. */
export interface ReplaySeatView {
  seat: number;
  displayName: string | null;
  isBot: boolean;
  concealed: TileView[];
  /** The concealed tiles as blocks. Empty except on frames where the hand has already ended. */
  groups: HandGroupView[];
  melds: MeldView[];
  bonus: TileView[];
  balance: number;
}

/** One step of a finished hand. */
export interface ReplayFrameView {
  index: number;
  afterSeq: number;
  /** What happened to produce this frame, e.g. "Ate Rose discarded 5 dots". Written server side. */
  caption: string;
  currentSeat: number;
  phase: GamePhase;
  tilesRemaining: number;
  seats: ReplaySeatView[];
  discards: DiscardView[];
  outcome: OutcomeView | null;
}

/** One finished hand, as listed on the replay index. */
export interface ReplayListItemView {
  handNumber: number;
  startedAt: string;
  endedAt: string | null;
  manoSeat: number;
  joker: TileCode | null;
  winnerSeat: number | null;
  winnerName: string | null;
  reason: string;
  totalUnits: number;
  frameCount: number;
}

export interface ReplayView {
  roomCode: string;
  handNumber: number;
  manoSeat: number;
  joker: TileCode | null;
  frames: ReplayFrameView[];
}

export interface ReplayUnlockResponse {
  token: string;
  expiresAt: string;
}

export interface MoveResult {
  success: boolean;
  error: string | null;
  detail: string | null;
}

/** Human-readable names for the things the server reports in a score breakdown. */
export const BONUS_LABELS: Record<string, string> = {
  Todas: 'Todas',
  Escalera: 'Escalera (1-9 run)',
  SietePares: 'Siete pares (7 pairs)',
  Concealed: 'All up (nothing shown)',
  AllExposed: 'All down',
  Flush: 'Flush (one suit)',
  AllPungs: 'All pungs',
  AllChows: 'All chows',
  QuickWin: 'Quick win',
  Single: 'Single wait',
  Paningit: 'Paningit (middle of a run)',
  BackToBack: 'Back to back',
  Bisaklat: 'Bisaklat',
};

export const AMBITION_LABELS: Record<string, string> = {
  NoFlowers: 'No flowers',
  Kang: 'Kang',
  SecretKang: 'Secret kang',
  Sagasa: 'Sagasa',
};
