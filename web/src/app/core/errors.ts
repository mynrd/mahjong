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
    case 'UsernameTaken':
      return 'That username is taken. Usernames are first come, first served.';
    case 'BadUsername':
      return 'Between 3 and 24 characters: letters, numbers, and . _ - only.';
    case 'WeakPassword':
      return 'Use at least 8 characters.';
    case 'BadCredentials':
      return 'That username and password do not match.';
    case 'NotSignedIn':
      return 'Sign in to see your games.';
    default:
      return body?.error ?? fallback;
  }
}
