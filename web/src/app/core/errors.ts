/** Pulls the server's error code out of an HttpErrorResponse, falling back to a plain message. */
export function readError(error: unknown, fallback: string): string {
  const body = (error as { error?: { error?: string; detail?: string } })?.error;
  if (body?.detail) return body.detail;

  switch (body?.error) {
    case 'WrongPassword':
      return 'That password does not match this table.';
    case 'RoomNotFound':
      return 'No table with that code.';
    case 'RoomFull':
      return 'All four seats are taken.';
    case 'RoomClosed':
      return 'That table has been closed.';
    case 'NotEnoughPlayers':
      return 'All four seats have to be filled first.';
    case 'HostOnly':
      return 'Only the player who made the table can do that.';
    case 'HandInProgress':
      return 'A hand is already being played.';
    default:
      return body?.error ?? fallback;
  }
}
