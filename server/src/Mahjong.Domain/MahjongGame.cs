using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Mahjong.Domain;

public sealed class IllegalMoveException : InvalidOperationException
{
	public string? Code { get; }

	public IllegalMoveException(string message, string? code = null)
		: base(message)
	{
		Code = code;
	}
}
public sealed record ClaimCandidate(ClaimKind Kind, IReadOnlyList<TileRef> Support)
{
	public string Describe(TileRef discard)
	{
		if (Kind == ClaimKind.Todas)
		{
			return "Todas";
		}
		IEnumerable<string> values = from t in Support.Append(discard)
			select t.Tile into t
			orderby t.Suit, t.Rank
			select t.Code;
		return (Kind == ClaimKind.Chow) ? ("Chow " + string.Join("-", values)) : $"{Kind} {discard.Tile.Code}";
	}
}
public static class MahjongGame
{
	public const int Seats = 4;

	public const int HandSize = 16;

	public static (GameState State, List<GameEvent> Events) Deal(RuleOptions rules, int handNumber, int manoSeat, int seed, DateTimeOffset now)
	{
		Random random = new Random(seed);
		List<TileRef> list = TileSet.All.ToList();
		Shuffle(list, random);
		GameState gameState = new GameState
		{
			Rules = rules,
			HandNumber = handNumber,
			ManoSeat = manoSeat,
			Seed = seed,
			Wall = list,
			FrontIndex = 0,
			BackIndex = 143,
			CurrentSeat = manoSeat
		};
		List<GameEvent> list2 = new List<GameEvent>
		{
			new HandDealt(handNumber, manoSeat)
		};
		for (int i = 0; i < 16; i++)
		{
			for (int j = 0; j < 4; j++)
			{
				gameState.Hands[(manoSeat + j) % 4].Concealed.Add(gameState.Wall[gameState.FrontIndex++]);
			}
		}
		gameState.Hands[manoSeat].Concealed.Add(gameState.Wall[gameState.FrontIndex++]);
		if (rules.JokerEnabled)
		{
			gameState.Joker = Tile.FromPlayableIndex(random.Next(34));
			list2.Add(new JokerChosen(gameState.Joker.Value));
		}
		for (int k = 0; k < 4; k++)
		{
			int seat = (manoSeat + k) % 4;
			list2.AddRange(ReplaceBonusTiles(gameState, seat));
		}
		foreach (int item in Enumerable.Range(0, 4))
		{
			int num = (manoSeat + item) % 4;
			if (gameState.Hands[num].Bonus.Count == 0)
			{
				list2.Add(PayAmbition(gameState, num, Ambition.NoFlowers));
			}
		}
		gameState.Phase = GamePhase.AwaitingDiscard;
		List<TileRef> concealed = gameState.Hands[manoSeat].Concealed;
		gameState.JustDrew = concealed[concealed.Count - 1];
		PlayerHand playerHand = gameState.Hands[manoSeat];
		if (HandAnalyzer.Analyze(playerHand.Concealed, playerHand.Melds, gameState.Joker, rules).IsWin)
		{
			list2.Add(EndWithWin(gameState, manoSeat, null, gameState.JustDrew.Value.Tile, bisaklat: true));
			return (State: gameState, Events: list2);
		}
		list2.Add(new TurnChanged(GamePhase.AwaitingDiscard)
		{
			Seat = manoSeat
		});
		return (State: gameState, Events: list2);
	}

	public static List<GameEvent> Draw(GameState state, int seat, DateTimeOffset now)
	{
		List<GameEvent> list = new List<GameEvent>();
		if (state.Phase == GamePhase.AwaitingClaims)
		{
			PendingClaim pending = state.Pending;
			if (pending != null && seat == GameState.NextSeat(state.CurrentSeat))
			{
				Require(!pending.Declared.Any<KeyValuePair<int, DeclaredClaim>>((KeyValuePair<int, DeclaredClaim> kv) => kv.Key != seat && kv.Value.AwaitingTiles), "Somebody has called that tile and is still choosing which tiles it costs.");
				pending.Declared.Remove(seat);
				pending.Passed.Add(seat);
				Disarm(pending);
				list.AddRange(TryCloseClaimWindow(state, now, forced: true));
				if (state.Phase != GamePhase.AwaitingDraw || state.CurrentSeat != seat)
				{
					return list;
				}
			}
		}
		Require(state.Phase == GamePhase.AwaitingDraw, "There is nothing to draw right now.");
		Require(seat == state.CurrentSeat, $"It is seat {state.CurrentSeat}'s turn.");
		if (state.WallExhausted)
		{
			list.Add(EndAsDraw(state));
			return list;
		}
		TileRef tileRef = state.Wall[state.FrontIndex++];
		list.Add(new TileDrawn(tileRef, Replacement: false)
		{
			Seat = seat
		});
		if (tileRef.Tile.IsBonus)
		{
			state.Hands[seat].Bonus.Add(tileRef);
			list.Add(new BonusExposed(tileRef, state.Hands[seat].Bonus.Count)
			{
				Seat = seat
			});
			TileRef? tileRef2 = TakeReplacement(state, seat, list);
			if (!tileRef2.HasValue)
			{
				return list;
			}
			tileRef = tileRef2.Value;
		}
		state.Hands[seat].Concealed.Add(tileRef);
		state.JustDrew = tileRef;
		state.Phase = GamePhase.AwaitingDiscard;
		list.Add(new TurnChanged(GamePhase.AwaitingDiscard)
		{
			Seat = seat
		});
		return list;
	}

	public static List<GameEvent> Discard(GameState state, int seat, int tileId, DateTimeOffset now)
	{
		Require(state.Phase == GamePhase.AwaitingDiscard, "You cannot throw a tile right now.");
		Require(seat == state.CurrentSeat, $"It is seat {state.CurrentSeat}'s turn.");
		PlayerHand playerHand = state.Hands[seat];
		int num = playerHand.Concealed.FindIndex((TileRef t) => t.Id == tileId);
		Require(num >= 0, "That tile is not in your hand.");
		TileRef tileRef = playerHand.Concealed[num];
		Require(tileRef.Tile.IsPlayable, "A flower or season cannot be thrown.");
		playerHand.Concealed.RemoveAt(num);
		state.Discards.Add(new DiscardedTile(seat, tileRef));
		state.JustDrew = null;
		List<GameEvent> list = new List<GameEvent>
		{
			new TileDiscarded(tileRef)
			{
				Seat = seat
			}
		};
		IReadOnlyDictionary<int, IReadOnlyList<ClaimKind>> readOnlyDictionary = AllowedClaims(state, tileRef, seat);
		state.Phase = GamePhase.AwaitingClaims;
		state.Pending = new PendingClaim
		{
			Tile = tileRef,
			FromSeat = seat,
			OpenedUtc = now,
			DeadlineUtc = null
		};
		for (int num2 = 0; num2 < 4; num2++)
		{
			if (num2 != seat && state.BotSeats.Contains(num2) && !readOnlyDictionary.ContainsKey(num2))
			{
				state.Pending.Passed.Add(num2);
			}
		}
		list.Add(new ClaimWindowOpened(tileRef, seat, state.Pending.DeadlineUtc, readOnlyDictionary)
		{
			Seat = seat
		});
		list.AddRange(TryCloseClaimWindow(state, now, forced: false));
		return list;
	}

	private static void Arm(GameState state, PendingClaim pending, DateTimeOffset now)
	{
		DateTimeOffset? deadlineUtc = pending.DeadlineUtc;
		DateTimeOffset valueOrDefault = deadlineUtc.GetValueOrDefault();
		if (!deadlineUtc.HasValue)
		{
			valueOrDefault = now.AddSeconds(state.Rules.ClaimWindowSeconds);
			DateTimeOffset? deadlineUtc2 = valueOrDefault;
			pending.DeadlineUtc = deadlineUtc2;
		}
	}

	private static void Disarm(PendingClaim pending)
	{
		if (!pending.Declared.Values.Any((DeclaredClaim c) => !c.AwaitingTiles))
		{
			pending.DeadlineUtc = null;
		}
	}

	public static List<GameEvent> Pass(GameState state, int seat, DateTimeOffset now)
	{
		Require(state.Phase == GamePhase.AwaitingClaims && state.Pending != null, "There is no discard open to pass on.");
		Require(seat != state.Pending.FromSeat, "You threw that tile, so there is nothing to pass on.");
		state.Pending.Declared.Remove(seat);
		state.Pending.Passed.Add(seat);
		Disarm(state.Pending);
		return TryCloseClaimWindow(state, now, forced: false);
	}

	public static List<GameEvent> Claim(GameState state, int seat, ClaimKind kind, IReadOnlyList<int> tileIds, DateTimeOffset now)
	{
		Require(state.Phase == GamePhase.AwaitingClaims && state.Pending != null, "That tile is no longer up for a claim.");
		PendingClaim pending = state.Pending;
		Require(seat != pending.FromSeat, "You threw that tile, so you cannot claim it.");
		DeclaredClaim valueOrDefault = pending.Declared.GetValueOrDefault(seat);
		Require((object)valueOrDefault == null || !valueOrDefault.AwaitingTiles || valueOrDefault.Kind != kind || tileIds.Count > 0, $"You have already called {kind}. Now tap the tiles in your hand it costs.", "AlreadyPressed");
		bool condition = LiveKinds(state, pending, seat).Contains(kind);
		(int, ClaimKind, bool)? tuple = StandingCall(state, pending);
		object message;
		if (tuple.HasValue)
		{
			(int, ClaimKind, bool) valueOrDefault2 = tuple.GetValueOrDefault();
			message = $"{valueOrDefault2.Item2} has been called on that tile, and {kind} does not beat it.";
		}
		else
		{
			message = NoSuchClaim(kind);
		}
		Require(condition, (string)message, "Outranked");
		IReadOnlyList<ClaimCandidate> source = ClaimCandidates(state, pending.Tile, pending.FromSeat, seat);
		Require(source.Any((ClaimCandidate c) => c.Kind == kind), NoSuchClaim(kind), "CannotClaim");
		IReadOnlyList<int> readOnlyList = Array.Empty<int>();
		if (tileIds.Count > 0)
		{
			Require(tileIds.Distinct().Count() == tileIds.Count, "The same tile cannot be picked twice.");
			List<TileRef> source2 = ResolveHeld(state.Hands[seat], tileIds);
			List<string> picked = source2.Select((TileRef t) => t.Tile.Code).Order().ToList();
			Require(source.Any((ClaimCandidate c) => c.Kind == kind && c.Support.Select((TileRef t) => t.Tile.Code).Order().SequenceEqual(picked)), $"The tiles you picked do not make a {kind} with that discard.", "TilesDoNotMake");
			readOnlyList = source2.Select((TileRef t) => t.Id).ToList();
		}
		pending.Passed.Remove(seat);
		bool flag = !state.Rules.AssistEnabled && readOnlyList.Count == 0 && kind != ClaimKind.Todas;
		pending.Declared[seat] = new DeclaredClaim(kind, readOnlyList, flag);
		if (!flag)
		{
			Arm(state, pending, now);
		}
		DropOutranked(state, pending);
		return TryCloseClaimWindow(state, now, forced: false);
	}

	private static string NoSuchClaim(ClaimKind kind)
	{
		if (1 == 0)
		{
		}
		string result = kind switch
		{
			ClaimKind.Chow => "You cannot chow that tile.", 
			ClaimKind.Pung => "You cannot pung that tile: a pung needs two more of the same face in your hand.", 
			ClaimKind.Kang => "You cannot kang that tile: a kang needs three more of the same face in your hand.", 
			ClaimKind.Todas => "That tile does not finish your hand.", 
			_ => $"You cannot {kind} that tile.", 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	public static List<GameEvent> Withdraw(GameState state, int seat)
	{
		Require(state.Phase == GamePhase.AwaitingClaims && state.Pending != null, "There is no discard open to take a call back on.");
		PendingClaim pending = state.Pending;
		Require(pending.Declared.ContainsKey(seat), "You have no call to take back on this tile.");
		pending.Declared.Remove(seat);
		foreach (int item in pending.Outranked)
		{
			pending.Passed.Remove(item);
		}
		pending.Outranked.Clear();
		Disarm(pending);
		return new List<GameEvent>();
	}

	public static List<GameEvent> ExpireClaimWindow(GameState state, DateTimeOffset now)
	{
		if (state.Phase == GamePhase.AwaitingClaims)
		{
			PendingClaim pending = state.Pending;
			if (pending != null)
			{
				DateTimeOffset? deadlineUtc = pending.DeadlineUtc;
				if (deadlineUtc.HasValue)
				{
					DateTimeOffset valueOrDefault = deadlineUtc.GetValueOrDefault();
					if (!(valueOrDefault > now))
					{
						if (pending.Declared.Values.Any((DeclaredClaim c) => c.AwaitingTiles))
						{
							return new List<GameEvent>();
						}
						return TryCloseClaimWindow(state, now, forced: true);
					}
				}
				return new List<GameEvent>();
			}
		}
		return new List<GameEvent>();
	}

	public static List<GameEvent> DeclareSecretKang(GameState state, int seat, Tile face)
	{
		Require(state.Phase == GamePhase.AwaitingDiscard && seat == state.CurrentSeat, "You cannot declare a secret kang right now.");
		PlayerHand playerHand = state.Hands[seat];
		List<TileRef> list = playerHand.Concealed.Where((TileRef t) => t.Tile == face).Take(4).ToList();
		Require(list.Count == 4, "You do not hold four of those.");
		foreach (TileRef item in list)
		{
			playerHand.Concealed.Remove(item);
		}
		ExposedMeld exposedMeld = new ExposedMeld(SetKind.Kang, list, Concealed: true);
		playerHand.Melds.Add(exposedMeld);
		List<GameEvent> list2 = new List<GameEvent>
		{
			new MeldFormed(exposedMeld)
			{
				Seat = seat
			}
		};
		list2.Add(PayAmbition(state, seat, Ambition.SecretKang));
		TileRef? tileRef = TakeReplacement(state, seat, list2);
		if (!tileRef.HasValue)
		{
			return list2;
		}
		playerHand.Concealed.Add(tileRef.Value);
		state.JustDrew = tileRef.Value;
		list2.Add(new TurnChanged(GamePhase.AwaitingDiscard)
		{
			Seat = seat
		});
		return list2;
	}

	public static List<GameEvent> DeclareSagasa(GameState state, int seat, Tile face)
	{
		Require(state.Phase == GamePhase.AwaitingDiscard && seat == state.CurrentSeat, "You cannot declare sagasa right now.");
		PlayerHand playerHand = state.Hands[seat];
		int num = playerHand.Melds.FindIndex((ExposedMeld m) => m.Kind == SetKind.Pung && m.BaseTile == face);
		Require(num >= 0, "You have no pung of that tile on the table to extend.");
		TileRef tileRef = playerHand.Concealed.FirstOrDefault((TileRef t) => t.Tile == face, new TileRef(-1));
		Require(tileRef.Id >= 0, "You are not holding the fourth one.");
		playerHand.Concealed.Remove(tileRef);
		ExposedMeld exposedMeld = playerHand.Melds[num];
		ExposedMeld exposedMeld4 = exposedMeld with
		{
			Kind = SetKind.Kang,
			Tiles = [.. exposedMeld.Tiles, tileRef],
			FromSagasa = true
		};
		playerHand.Melds[num] = exposedMeld4;
		List<GameEvent> list = new List<GameEvent>
		{
			new MeldFormed(exposedMeld4)
			{
				Seat = seat
			}
		};
		list.Add(PayAmbition(state, seat, Ambition.Sagasa));
		TileRef? tileRef2 = TakeReplacement(state, seat, list);
		if (!tileRef2.HasValue)
		{
			return list;
		}
		playerHand.Concealed.Add(tileRef2.Value);
		state.JustDrew = tileRef2.Value;
		list.Add(new TurnChanged(GamePhase.AwaitingDiscard)
		{
			Seat = seat
		});
		return list;
	}

	public static List<GameEvent> DeclareTodasOnDraw(GameState state, int seat)
	{
		Require(state.Phase == GamePhase.AwaitingDiscard && seat == state.CurrentSeat, "You cannot declare todas right now.");
		Require(state.JustDrew.HasValue, "There is no drawn tile to win on.");
		PlayerHand playerHand = state.Hands[seat];
		Require(HandAnalyzer.Analyze(playerHand.Concealed, playerHand.Melds, state.Joker, state.Rules).IsWin, "Your hand is not complete.");
		int num = 1;
		List<GameEvent> list = new List<GameEvent>(num);
		CollectionsMarshal.SetCount(list, num);
		CollectionsMarshal.AsSpan(list)[0] = EndWithWin(state, seat, null, state.JustDrew.Value.Tile, bisaklat: false);
		return list;
	}

	public static IReadOnlyDictionary<int, IReadOnlyList<ClaimKind>> AllowedClaims(GameState state, TileRef discard, int fromSeat)
	{
		Dictionary<int, IReadOnlyList<ClaimKind>> dictionary = new Dictionary<int, IReadOnlyList<ClaimKind>>();
		for (int i = 0; i < 4; i++)
		{
			if (i != fromSeat)
			{
				List<ClaimKind> list = (from c in ClaimCandidates(state, discard, fromSeat, i)
					select c.Kind).Distinct().ToList();
				if (list.Count > 0)
				{
					dictionary[i] = list;
				}
			}
		}
		return dictionary;
	}

	public static IReadOnlyList<ClaimCandidate> ClaimCandidates(GameState state, TileRef discard, int fromSeat, int seat)
	{
		if (seat == fromSeat || seat < 0 || seat >= 4)
		{
			return Array.Empty<ClaimCandidate>();
		}
		PlayerHand playerHand = state.Hands[seat];
		Tile face = discard.Tile;
		List<ClaimCandidate> list = new List<ClaimCandidate>();
		if (CanWinOn(state, seat, face))
		{
			list.Add(new ClaimCandidate(ClaimKind.Todas, Array.Empty<TileRef>()));
		}
		List<TileRef> list2 = playerHand.Concealed.Where((TileRef t) => t.Tile == face).ToList();
		if (list2.Count >= 3)
		{
			list.Add(new ClaimCandidate(ClaimKind.Kang, list2.Take(3).ToList()));
		}
		if (list2.Count >= 2)
		{
			list.Add(new ClaimCandidate(ClaimKind.Pung, list2.Take(2).ToList()));
		}
		if (ChowPossible(state, discard, fromSeat, seat))
		{
			foreach (List<TileRef> item in RunPartners(playerHand, face))
			{
				list.Add(new ClaimCandidate(ClaimKind.Chow, item));
			}
		}
		return list;
	}

	public static bool ChowPossible(GameState state, TileRef discard, int fromSeat, int seat)
	{
		return seat != fromSeat && discard.Tile.IsSuited && (!state.Rules.ChowFromLeftOnly || GameState.IsLeftOf(seat, fromSeat));
	}

	private static bool CanWinOn(GameState state, int seat, Tile face)
	{
		PlayerHand playerHand = state.Hands[seat];
		List<Tile> concealed = playerHand.ConcealedFaces.Append(face).ToList();
		if (!HandAnalyzer.Analyze(concealed, playerHand.Melds, state.Joker, state.Rules).IsWin)
		{
			return false;
		}
		if (!state.Rules.JokerCanCompleteClaimedWin && state.Joker.HasValue)
		{
			return HandAnalyzer.Analyze(concealed, playerHand.Melds, null, state.Rules).IsWin;
		}
		return true;
	}

	private static List<List<TileRef>> RunPartners(PlayerHand hand, Tile face)
	{
		List<List<TileRef>> list = new List<List<TileRef>>();
		if (!face.IsSuited)
		{
			return list;
		}
		int[] array = new int[3]
		{
			face.Rank - 2,
			face.Rank - 1,
			face.Rank
		};
		foreach (int num in array)
		{
			if (num < 1 || num + 2 > 9)
			{
				continue;
			}
			List<int> list2 = new int[3]
			{
				num,
				num + 1,
				num + 2
			}.Where((int r) => r != face.Rank).ToList();
			if (list2.Count == 2)
			{
				TileRef? tileRef = Find(list2[0]);
				TileRef? tileRef2 = Find(list2[1]);
				if (tileRef.HasValue && tileRef2.HasValue)
				{
					int num2 = 2;
					List<TileRef> list3 = new List<TileRef>(num2);
					CollectionsMarshal.SetCount(list3, num2);
					Span<TileRef> span = CollectionsMarshal.AsSpan(list3);
					span[0] = tileRef.Value;
					span[1] = tileRef2.Value;
					list.Add(list3);
				}
			}
		}
		return list;
		TileRef? Find(int rank)
		{
			if ((rank < 1 || rank > 9) ? true : false)
			{
				return null;
			}
			Tile wanted = new Tile(face.Suit, rank);
			TileRef value = hand.Concealed.FirstOrDefault((TileRef t) => t.Tile == wanted, new TileRef(-1));
			return (value.Id >= 0) ? new TileRef?(value) : ((TileRef?)null);
		}
	}

	public static int ClaimRank(RuleOptions rules, ClaimKind kind)
	{
		if (1 == 0)
		{
		}
		int result = kind switch
		{
			ClaimKind.Todas => (!rules.TodasBeatsPungAndKang) ? 1 : 3, 
			ClaimKind.Kang => 2, 
			ClaimKind.Pung => 2, 
			ClaimKind.Chow => 0, 
			_ => -1, 
		};
		if (1 == 0)
		{
		}
		return result;
	}

	public static (int Seat, ClaimKind Kind, bool AwaitingTiles)? StandingCall(GameState state, PendingClaim pending)
	{
		if (pending.Declared.Count == 0)
		{
			return null;
		}
		KeyValuePair<int, DeclaredClaim> keyValuePair = (from kv in pending.Declared
			orderby ClaimRank(state.Rules, kv.Value.Kind) descending, (kv.Key - pending.FromSeat + 4) % 4
			select kv).First();
		return (keyValuePair.Key, keyValuePair.Value.Kind, keyValuePair.Value.AwaitingTiles);
	}

	public static IReadOnlyList<ClaimKind> LiveKinds(GameState state, PendingClaim pending, int seat)
	{
		ClaimKind[] array = new ClaimKind[4]
		{
			ClaimKind.Chow,
			ClaimKind.Pung,
			ClaimKind.Kang,
			ClaimKind.Todas
		};
		if (seat == pending.FromSeat)
		{
			return Array.Empty<ClaimKind>();
		}
		if (pending.Outranked.Contains(seat))
		{
			return Array.Empty<ClaimKind>();
		}
		(int, ClaimKind, bool)? tuple = StandingCall(state, pending);
		if (tuple.HasValue)
		{
			(int Seat, ClaimKind Kind, bool AwaitingTiles) standing = tuple.GetValueOrDefault();
			if (standing.Seat != seat)
			{
				int bar = ClaimRank(state.Rules, standing.Kind);
				return array.Where(delegate(ClaimKind kind)
				{
					int num = ClaimRank(state.Rules, kind);
					if (num != bar)
					{
						return num > bar;
					}
					ClaimKind item = standing.Kind;
					bool flag = (uint)(item - 1) <= 1u;
					return !flag;
				}).ToList();
			}
		}
		return array;
	}

	private static void DropOutranked(GameState state, PendingClaim pending)
	{
		int num = (from kv in pending.Declared
			where !kv.Value.AwaitingTiles
			select ClaimRank(state.Rules, kv.Value.Kind)).DefaultIfEmpty(int.MinValue).Max();
		foreach (var (num3, declaredClaim2) in pending.Declared.ToList())
		{
			if (ClaimRank(state.Rules, declaredClaim2.Kind) < num)
			{
				pending.Declared.Remove(num3);
				pending.Outranked.Add(num3);
				pending.Passed.Add(num3);
			}
		}
	}

	private static List<GameEvent> TryCloseClaimWindow(GameState state, DateTimeOffset now, bool forced)
	{
		PendingClaim pending = state.Pending;
		if (!forced && pending.Declared.Values.Any((DeclaredClaim c) => c.AwaitingTiles))
		{
			return new List<GameEvent>();
		}
		int num = pending.Declared.Count + pending.Passed.Count;
		if (!forced && num < 3)
		{
			return new List<GameEvent>();
		}
		foreach (int item in (from kv in pending.Declared
			where kv.Value.AwaitingTiles
			select kv.Key).ToList())
		{
			pending.Declared.Remove(item);
			pending.Outranked.Add(item);
			pending.Passed.Add(item);
		}
		if (pending.Declared.Count == 0)
		{
			state.Pending = null;
			List<GameEvent> list = new List<GameEvent>
			{
				new ClaimWindowClosed(pending.Tile)
			};
			list.AddRange(AdvanceTurn(state));
			return list;
		}
		(int, DeclaredClaim) tuple = PickWinningClaim(state, pending);
		return ApplyClaim(state, tuple.Item1, tuple.Item2, pending);
	}

	private static (int Seat, DeclaredClaim Claim) PickWinningClaim(GameState state, PendingClaim pending)
	{
		return (from kv in pending.Declared
			orderby ClaimRank(state.Rules, kv.Value.Kind) descending, Distance(kv.Key)
			select (Seat: kv.Key, Claim: kv.Value)).First();
		int Distance(int seat)
		{
			return (seat - pending.FromSeat + 4) % 4;
		}
	}

	private static List<GameEvent> ApplyClaim(GameState state, int seat, DeclaredClaim declared, PendingClaim pending)
	{
		ClaimKind kind = declared.Kind;
		PlayerHand playerHand = state.Hands[seat];
		Tile face = pending.Tile.Tile;
		List<GameEvent> list = new List<GameEvent>();
		int index = state.Discards.Count - 1;
		state.Discards[index] = state.Discards[index]with
		{
			Claimed = true
		};
		state.Pending = null;
		if (kind == ClaimKind.Todas)
		{
			list.Add(EndWithWin(state, seat, pending.FromSeat, pending.Tile.Tile, bisaklat: false));
			return list;
		}
		if (1 == 0)
		{
		}
		int num = kind switch
		{
			ClaimKind.Kang => 3, 
			ClaimKind.Pung => 2, 
			ClaimKind.Chow => 2, 
			_ => throw new IllegalMoveException($"Cannot apply claim {kind}."), 
		};
		if (1 == 0)
		{
		}
		int num2 = num;
		List<TileRef> list2;
		if (declared.TileIds.Count > 0)
		{
			list2 = ResolveHeld(playerHand, declared.TileIds);
			if (list2.Count != num2)
			{
				throw new IllegalMoveException($"Seat {seat} named {list2.Count} tiles for a {kind}, which needs {num2}.");
			}
		}
		else if (kind == ClaimKind.Chow)
		{
			list2 = RunPartners(playerHand, face).FirstOrDefault() ?? throw new IllegalMoveException($"Seat {seat} can no longer form a run with {face}.");
		}
		else
		{
			list2 = playerHand.Concealed.Where((TileRef t) => t.Tile == face).Take(num2).ToList();
			if (list2.Count != num2)
			{
				throw new IllegalMoveException($"Seat {seat} no longer holds {num2} copies of {face}.");
			}
		}
		foreach (TileRef item in list2)
		{
			playerHand.Concealed.Remove(item);
		}
		int kind2 = kind switch
		{
			ClaimKind.Kang => 3, 
			ClaimKind.Chow => 1, 
			_ => 2, 
		};
		List<TileRef> list3 = list2;
		num = 0;
		TileRef[] array = new TileRef[1 + list3.Count];
		Span<TileRef> span = CollectionsMarshal.AsSpan(list3);
		span.CopyTo(new Span<TileRef>(array).Slice(num, span.Length));
		num += span.Length;
		array[num] = pending.Tile;
		ExposedMeld exposedMeld = new ExposedMeld((SetKind)kind2, array, Concealed: false, pending.FromSeat);
		playerHand.Melds.Add(exposedMeld);
		list.Add(new MeldFormed(exposedMeld)
		{
			Seat = seat
		});
		state.CurrentSeat = seat;
		if (kind == ClaimKind.Kang)
		{
			list.Add(PayAmbition(state, seat, Ambition.Kang));
			TileRef? tileRef = TakeReplacement(state, seat, list);
			if (!tileRef.HasValue)
			{
				return list;
			}
			playerHand.Concealed.Add(tileRef.Value);
			state.JustDrew = tileRef.Value;
		}
		else
		{
			state.JustDrew = null;
		}
		state.Phase = GamePhase.AwaitingDiscard;
		list.Add(new TurnChanged(GamePhase.AwaitingDiscard)
		{
			Seat = seat
		});
		return list;
	}

	private static List<GameEvent> AdvanceTurn(GameState state)
	{
		List<GameEvent> list = new List<GameEvent>();
		state.CurrentSeat = GameState.NextSeat(state.CurrentSeat);
		state.JustDrew = null;
		if (state.WallExhausted)
		{
			list.Add(EndAsDraw(state));
			return list;
		}
		state.Phase = GamePhase.AwaitingDraw;
		list.Add(new TurnChanged(GamePhase.AwaitingDraw)
		{
			Seat = state.CurrentSeat
		});
		return list;
	}

	private static List<GameEvent> ReplaceBonusTiles(GameState state, int seat)
	{
		List<GameEvent> list = new List<GameEvent>();
		PlayerHand playerHand = state.Hands[seat];
		while (true)
		{
			int num = playerHand.Concealed.FindIndex((TileRef t) => t.Tile.IsBonus);
			if (num < 0)
			{
				break;
			}
			TileRef tileRef = playerHand.Concealed[num];
			playerHand.Concealed.RemoveAt(num);
			playerHand.Bonus.Add(tileRef);
			list.Add(new BonusExposed(tileRef, playerHand.Bonus.Count)
			{
				Seat = seat
			});
			TileRef? tileRef2 = TakeReplacement(state, seat, list);
			if (!tileRef2.HasValue)
			{
				break;
			}
			playerHand.Concealed.Add(tileRef2.Value);
		}
		return list;
	}

	private static TileRef? TakeReplacement(GameState state, int seat, List<GameEvent> events)
	{
		TileRef tileRef;
		while (true)
		{
			if (state.WallExhausted)
			{
				events.Add(EndAsDraw(state));
				return null;
			}
			tileRef = state.Wall[state.BackIndex--];
			events.Add(new TileDrawn(tileRef, Replacement: true)
			{
				Seat = seat
			});
			if (!tileRef.Tile.IsBonus)
			{
				break;
			}
			PlayerHand playerHand = state.Hands[seat];
			playerHand.Bonus.Add(tileRef);
			events.Add(new BonusExposed(tileRef, playerHand.Bonus.Count)
			{
				Seat = seat
			});
		}
		return tileRef;
	}

	private static AmbitionEarned PayAmbition(GameState state, int seat, Ambition ambition)
	{
		return new AmbitionEarned(ambition, Scorer.SettleAmbition(ambition, seat, state.Rules))
		{
			Seat = seat
		};
	}

	private static HandEnded EndWithWin(GameState state, int winnerSeat, int? discarderSeat, Tile winningTile, bool bisaklat)
	{
		PlayerHand playerHand = state.Hands[winnerSeat];
		List<Tile> list = playerHand.ConcealedFaces.ToList();
		if (!discarderSeat.HasValue)
		{
			int num = list.IndexOf(winningTile);
			if (num >= 0)
			{
				list.RemoveAt(num);
			}
		}
		WinInput input = new WinInput(list, playerHand.Melds, winningTile, !discarderSeat.HasValue, state.Discards.Count((DiscardedTile d) => !d.Claimed), state.Joker, bisaklat);
		HandScore score = Scorer.Score(input, state.Rules);
		IReadOnlyList<Settlement> settlements = Scorer.Settle(score, winnerSeat, discarderSeat, state.Rules);
		HandOutcome outcome = (state.Outcome = new HandOutcome(bisaklat ? HandEndReason.Bisaklat : HandEndReason.Todas, winnerSeat, score, settlements));
		state.Phase = GamePhase.HandOver;
		state.Pending = null;
		return new HandEnded(outcome)
		{
			Seat = winnerSeat
		};
	}

	private static HandEnded EndAsDraw(GameState state)
	{
		HandOutcome outcome = (state.Outcome = new HandOutcome(HandEndReason.WallExhausted, null, null, Array.Empty<Settlement>()));
		state.Phase = GamePhase.HandOver;
		state.Pending = null;
		return new HandEnded(outcome);
	}

	private static List<TileRef> ResolveHeld(PlayerHand hand, IReadOnlyList<int> tileIds)
	{
		List<TileRef> list = new List<TileRef>(tileIds.Count);
		foreach (int id in tileIds)
		{
			TileRef item = hand.Concealed.FirstOrDefault((TileRef t) => t.Id == id, new TileRef(-1));
			if (item.Id < 0)
			{
				throw new IllegalMoveException($"Tile {id} is not in hand.");
			}
			list.Add(item);
		}
		return list;
	}

	private static void Shuffle<T>(IList<T> items, Random rng)
	{
		for (int num = items.Count - 1; num > 0; num--)
		{
			int num2 = rng.Next(num + 1);
			int index = num;
			int index2 = num2;
			T value = items[num2];
			T value2 = items[num];
			items[index] = value;
			items[index2] = value2;
		}
	}

	private static void Require([DoesNotReturnIf(false)] bool condition, string message, string? code = null)
	{
		if (!condition)
		{
			throw new IllegalMoveException(message, code);
		}
	}
}
