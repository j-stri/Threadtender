using Microsoft.Data.Sqlite;

namespace ThreadTender;

public record PendingOrphan(
	ulong MessageId,
	ulong ChannelId,
	ulong GuildId,
	ulong AuthorId,
	string Content,
	string Notes,
	string AuthorName,
	ulong PromptMessageId,
	ulong PromptChannelId, // where the prompt lives: a DM channel, or 0 = the managed channel (fallback)
	DateTimeOffset ExpiresAt);

/// <summary>A comment recorded as part of a top-level post's reply graph (flex mode).</summary>
public record GraphEdge(ulong MessageId, ulong AuthorId, string AuthorName);

/// <summary>
/// A retroactive-threading session. Status runs drafting → staging → reviewing →
/// confirming → finalizing → done, or ends in cancelled. AnchorId is 0 until picked;
/// ThreadId is 0 until staged. Standalone = the anchor already had a thread, so this
/// import lives in a thread not attached to any message, opened by a copy of the
/// anchor (AnchorCopyIds: its message IDs, comma-separated; "" otherwise).
/// </summary>
public record RetroSession(
	ulong SessionId,
	ulong GuildId,
	ulong ChannelId,
	ulong AnchorId,
	ulong InvokerId,
	string ThreadName,
	ulong ThreadId,
	ulong HeaderMessageId,
	ulong ConfirmMessageId,
	string Status,
	bool Standalone,
	string AnchorCopyIds,
	DateTimeOffset ExpiresAt)
{
	public bool IsActive => Status is "drafting" or "staging" or "reviewing" or "confirming" or "finalizing";

	/// <summary>Every message of the standalone anchor copy (a long anchor spans several).</summary>
	public IEnumerable<ulong> AnchorCopyIdList => AnchorCopyIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ulong.Parse);
}

/// <summary>
/// One candidate message in a retro session, in channel order. Verdict is pending,
/// keep, drop (copy removed, original stays), stay (copy kept but the original stays
/// too, because the copy couldn't carry everything) or moved (Finalize is deleting the
/// original). CopyId is 0 until staged; ExtraCopyIds lists further chunks of a copy
/// too long for one message. ReplyHeader is the link header the copy was posted with
/// ("" for none), kept so edit reconciliation can rebuild the copy's text.
/// </summary>
public record RetroItem(
	ulong SessionId,
	ulong OriginalId,
	int Position,
	ulong AuthorId,
	string AuthorName,
	bool Included,
	ulong CopyId,
	string Verdict,
	string ReplyHeader,
	string ExtraCopyIds)
{
	public IEnumerable<ulong> AllCopyIds => CopyId == 0 ? [] :
		[CopyId, .. ExtraCopyIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(ulong.Parse)];
}

/// <summary>
/// SQLite persistence: pending orphaned comments (so a restart doesn't eat anyone's
/// message), a map of transposed-message IDs to their destination (so replies aimed at
/// an already-moved message can still be routed; thread_id 0 = moved back into the
/// channel rather than into a thread), reply-graph membership for flex-mode counting,
/// and the root→thread map for flex posts that have already been swept.
/// </summary>
public class Database : IDisposable
{
	private readonly SqliteConnection connection;
	private readonly object gate = new();

	public Database(string path)
	{
		connection = new SqliteConnection($"Data Source={path}");
		connection.Open();
		Execute("""
			CREATE TABLE IF NOT EXISTS pending_orphans (
				message_id INTEGER PRIMARY KEY,
				channel_id INTEGER NOT NULL,
				guild_id INTEGER NOT NULL,
				author_id INTEGER NOT NULL,
				content TEXT NOT NULL,
				notes TEXT NOT NULL DEFAULT '',
				author_name TEXT NOT NULL DEFAULT '',
				prompt_message_id INTEGER NOT NULL DEFAULT 0,
				prompt_channel_id INTEGER NOT NULL DEFAULT 0,
				expires_at TEXT NOT NULL
			);
			CREATE TABLE IF NOT EXISTS transposed (
				original_message_id INTEGER PRIMARY KEY,
				thread_id INTEGER NOT NULL,
				created_at TEXT NOT NULL DEFAULT (datetime('now'))
			);
			CREATE TABLE IF NOT EXISTS graph_edges (
				message_id INTEGER PRIMARY KEY,
				root_id INTEGER NOT NULL,
				channel_id INTEGER NOT NULL,
				author_id INTEGER NOT NULL,
				author_name TEXT NOT NULL DEFAULT '',
				line_count INTEGER NOT NULL DEFAULT 0
			);
			CREATE INDEX IF NOT EXISTS idx_graph_edges_root ON graph_edges(root_id);
			CREATE TABLE IF NOT EXISTS flex_threads (
				root_id INTEGER PRIMARY KEY,
				thread_id INTEGER NOT NULL
			);
			CREATE TABLE IF NOT EXISTS bindings (
				channel_id INTEGER PRIMARY KEY,
				guild_id INTEGER NOT NULL,
				mode TEXT NOT NULL,
				whitelist TEXT NOT NULL DEFAULT '',
				flex_threshold INTEGER NOT NULL DEFAULT 5,
				flex_line_threshold INTEGER NOT NULL DEFAULT 0
			);
			CREATE TABLE IF NOT EXISTS guild_settings (
				guild_id INTEGER NOT NULL,
				key TEXT NOT NULL,
				value TEXT NOT NULL,
				PRIMARY KEY (guild_id, key)
			);
			CREATE TABLE IF NOT EXISTS retro_sessions (
				session_id INTEGER PRIMARY KEY,
				guild_id INTEGER NOT NULL,
				channel_id INTEGER NOT NULL,
				anchor_id INTEGER NOT NULL DEFAULT 0,
				invoker_id INTEGER NOT NULL,
				thread_name TEXT NOT NULL,
				thread_id INTEGER NOT NULL DEFAULT 0,
				header_message_id INTEGER NOT NULL DEFAULT 0,
				confirm_message_id INTEGER NOT NULL DEFAULT 0,
				status TEXT NOT NULL,
				standalone INTEGER NOT NULL DEFAULT 0,
				anchor_copy_ids TEXT NOT NULL DEFAULT '',
				expires_at TEXT NOT NULL
			);
			CREATE TABLE IF NOT EXISTS retro_items (
				session_id INTEGER NOT NULL,
				original_id INTEGER NOT NULL,
				position INTEGER NOT NULL,
				author_id INTEGER NOT NULL,
				author_name TEXT NOT NULL DEFAULT '',
				included INTEGER NOT NULL DEFAULT 1,
				copy_id INTEGER NOT NULL DEFAULT 0,
				verdict TEXT NOT NULL DEFAULT 'pending',
				reply_header TEXT NOT NULL DEFAULT '',
				extra_copy_ids TEXT NOT NULL DEFAULT '',
				PRIMARY KEY (session_id, original_id)
			);
			CREATE INDEX IF NOT EXISTS idx_retro_items_copy ON retro_items(copy_id);
			CREATE INDEX IF NOT EXISTS idx_retro_items_original ON retro_items(original_id);
			""");

		// pending_orphans.author_name postdates the first release; graft it onto older DBs.
		try { Execute("ALTER TABLE pending_orphans ADD COLUMN author_name TEXT NOT NULL DEFAULT ''"); }
		catch (SqliteException) { /* column already exists */ }

		// (A short-lived bindings.repost_style column may exist in DBs from early builds;
		// it is simply ignored — repost style is contextual now, not configured.)

		// Dual flex thresholds postdate the original tables; graft onto older DBs.
		try { Execute("ALTER TABLE bindings ADD COLUMN flex_line_threshold INTEGER NOT NULL DEFAULT 0"); }
		catch (SqliteException) { /* column already exists */ }
		try { Execute("ALTER TABLE graph_edges ADD COLUMN line_count INTEGER NOT NULL DEFAULT 0"); }
		catch (SqliteException) { /* column already exists */ }

		// Retro columns added during development; graft onto any DB an earlier build made.
		try { Execute("ALTER TABLE retro_sessions ADD COLUMN anchor_copy_ids TEXT NOT NULL DEFAULT ''"); }
		catch (SqliteException) { /* column already exists */ }
		try { Execute("ALTER TABLE retro_items ADD COLUMN extra_copy_ids TEXT NOT NULL DEFAULT ''"); }
		catch (SqliteException) { /* column already exists */ }

		// DMed prompts postdate the original pending_orphans shape.
		try { Execute("ALTER TABLE pending_orphans ADD COLUMN prompt_channel_id INTEGER NOT NULL DEFAULT 0"); }
		catch (SqliteException) { /* column already exists */ }
	}

	private void Execute(string sql, params (string, object)[] args)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = sql;
			foreach ((string name, object value) in args)
				cmd.Parameters.AddWithValue(name, value);
			cmd.ExecuteNonQuery();
		}
	}

	// ---------------------------------------------------------------- pending orphans

	public void UpsertPending(PendingOrphan p) => Execute(
		"""
		INSERT INTO pending_orphans (message_id, channel_id, guild_id, author_id, content, notes, author_name, prompt_message_id, prompt_channel_id, expires_at)
		VALUES ($mid, $cid, $gid, $aid, $content, $notes, $aname, $pmid, $pcid, $exp)
		ON CONFLICT(message_id) DO UPDATE SET prompt_message_id = $pmid, prompt_channel_id = $pcid, expires_at = $exp
		""",
		("$mid", (long)p.MessageId), ("$cid", (long)p.ChannelId), ("$gid", (long)p.GuildId),
		("$aid", (long)p.AuthorId), ("$content", p.Content), ("$notes", p.Notes),
		("$aname", p.AuthorName), ("$pmid", (long)p.PromptMessageId), ("$pcid", (long)p.PromptChannelId),
		("$exp", p.ExpiresAt.ToString("O")));

	public void DeletePending(ulong messageId) =>
		Execute("DELETE FROM pending_orphans WHERE message_id = $mid", ("$mid", (long)messageId));

	public List<PendingOrphan> GetAllPending()
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT message_id, channel_id, guild_id, author_id, content, notes, author_name, prompt_message_id, prompt_channel_id, expires_at FROM pending_orphans";
			using SqliteDataReader reader = cmd.ExecuteReader();
			List<PendingOrphan> result = [];
			while (reader.Read())
			{
				result.Add(new PendingOrphan(
					(ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), (ulong)reader.GetInt64(2),
					(ulong)reader.GetInt64(3), reader.GetString(4), reader.GetString(5),
					reader.GetString(6), (ulong)reader.GetInt64(7), (ulong)reader.GetInt64(8),
					DateTimeOffset.Parse(reader.GetString(9))));
			}
			return result;
		}
	}

	// ---------------------------------------------------------------- transposed map

	public void RecordTransposed(ulong originalMessageId, ulong threadId) => Execute(
		"""
		INSERT INTO transposed (original_message_id, thread_id) VALUES ($mid, $tid)
		ON CONFLICT(original_message_id) DO UPDATE SET thread_id = $tid
		""",
		("$mid", (long)originalMessageId), ("$tid", (long)threadId));

	public void DeleteTransposed(ulong originalMessageId) =>
		Execute("DELETE FROM transposed WHERE original_message_id = $mid", ("$mid", (long)originalMessageId));

	public ulong? GetTransposedThread(ulong originalMessageId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT thread_id FROM transposed WHERE original_message_id = $mid";
			cmd.Parameters.AddWithValue("$mid", (long)originalMessageId);
			object? result = cmd.ExecuteScalar();
			return result is long l ? (ulong)l : null;
		}
	}

	// ---------------------------------------------------------------- flex reply graphs

	public void AddGraphEdge(ulong messageId, ulong rootId, ulong channelId, ulong authorId, string authorName, int lineCount) => Execute(
		"""
		INSERT INTO graph_edges (message_id, root_id, channel_id, author_id, author_name, line_count)
		VALUES ($mid, $rid, $cid, $aid, $aname, $lines)
		ON CONFLICT(message_id) DO NOTHING
		""",
		("$mid", (long)messageId), ("$rid", (long)rootId), ("$cid", (long)channelId),
		("$aid", (long)authorId), ("$aname", authorName), ("$lines", lineCount));

	public void DeleteGraphEdge(ulong messageId) =>
		Execute("DELETE FROM graph_edges WHERE message_id = $mid", ("$mid", (long)messageId));

	/// <summary>The recorded edge for a single comment, if any — including its EFFECTIVE author.</summary>
	public GraphEdge? GetGraphEdge(ulong messageId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT message_id, author_id, author_name FROM graph_edges WHERE message_id = $mid";
			cmd.Parameters.AddWithValue("$mid", (long)messageId);
			using SqliteDataReader reader = cmd.ExecuteReader();
			return reader.Read()
				? new GraphEdge((ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), reader.GetString(2))
				: null;
		}
	}

	public ulong? GetGraphRoot(ulong messageId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT root_id FROM graph_edges WHERE message_id = $mid";
			cmd.Parameters.AddWithValue("$mid", (long)messageId);
			object? result = cmd.ExecuteScalar();
			return result is long l ? (ulong)l : null;
		}
	}

	/// <summary>Message count and total line count recorded against a root's reply graph.</summary>
	public (int Messages, int Lines) GetGraphStats(ulong rootId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT COUNT(*), COALESCE(SUM(line_count), 0) FROM graph_edges WHERE root_id = $rid";
			cmd.Parameters.AddWithValue("$rid", (long)rootId);
			using SqliteDataReader reader = cmd.ExecuteReader();
			reader.Read();
			return (reader.GetInt32(0), reader.GetInt32(1));
		}
	}

	/// <summary>All recorded comments for a root, oldest first (message IDs are chronological snowflakes).</summary>
	public List<GraphEdge> GetGraphEdges(ulong rootId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT message_id, author_id, author_name FROM graph_edges WHERE root_id = $rid ORDER BY message_id";
			cmd.Parameters.AddWithValue("$rid", (long)rootId);
			using SqliteDataReader reader = cmd.ExecuteReader();
			List<GraphEdge> result = [];
			while (reader.Read())
				result.Add(new GraphEdge((ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1), reader.GetString(2)));
			return result;
		}
	}

	// ---------------------------------------------------------------- flex root→thread map

	public void SetFlexThread(ulong rootId, ulong threadId) => Execute(
		"""
		INSERT INTO flex_threads (root_id, thread_id) VALUES ($rid, $tid)
		ON CONFLICT(root_id) DO UPDATE SET thread_id = $tid
		""",
		("$rid", (long)rootId), ("$tid", (long)threadId));

	public ulong? GetFlexThread(ulong rootId)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT thread_id FROM flex_threads WHERE root_id = $rid";
			cmd.Parameters.AddWithValue("$rid", (long)rootId);
			object? result = cmd.ExecuteScalar();
			return result is long l ? (ulong)l : null;
		}
	}

	// ---------------------------------------------------------------- bindings

	public void UpsertBinding(ChannelBinding b) => Execute(
		"""
		INSERT INTO bindings (channel_id, guild_id, mode, whitelist, flex_threshold, flex_line_threshold)
		VALUES ($cid, $gid, $mode, $wl, $flex, $flexLines)
		ON CONFLICT(channel_id) DO UPDATE SET guild_id = $gid, mode = $mode, whitelist = $wl, flex_threshold = $flex, flex_line_threshold = $flexLines
		""",
		("$cid", (long)b.ChannelId), ("$gid", (long)b.GuildId), ("$mode", b.Mode.ToConfigString()),
		("$wl", string.Join(",", b.Whitelist)), ("$flex", b.FlexThreshold), ("$flexLines", b.FlexLineThreshold));

	public void DeleteBinding(ulong channelId) =>
		Execute("DELETE FROM bindings WHERE channel_id = $cid", ("$cid", (long)channelId));

	public List<ChannelBinding> GetAllBindings()
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT channel_id, guild_id, mode, whitelist, flex_threshold, flex_line_threshold FROM bindings";
			using SqliteDataReader reader = cmd.ExecuteReader();
			List<ChannelBinding> result = [];
			while (reader.Read())
			{
				List<ulong> whitelist = reader.GetString(3)
					.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Select(ulong.Parse).ToList();
				result.Add(new ChannelBinding(
					(ulong)reader.GetInt64(0), (ulong)reader.GetInt64(1),
					ChannelModes.Parse(reader.GetString(2)), whitelist, reader.GetInt32(4), reader.GetInt32(5)));
			}
			return result;
		}
	}

	public void DeleteGraphEdgesForChannel(ulong channelId) =>
		Execute("DELETE FROM graph_edges WHERE channel_id = $cid", ("$cid", (long)channelId));

	// ---------------------------------------------------------------- settings (per guild)
	// (A legacy global `settings` table may exist in DBs from early builds; it is
	// ignored — bot settings are per-guild now.)

	public void SetGuildSetting(ulong guildId, string key, string value) => Execute(
		"""
		INSERT INTO guild_settings (guild_id, key, value) VALUES ($g, $k, $v)
		ON CONFLICT(guild_id, key) DO UPDATE SET value = $v
		""",
		("$g", (long)guildId), ("$k", key), ("$v", value));

	public string? GetGuildSetting(ulong guildId, string key)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = "SELECT value FROM guild_settings WHERE guild_id = $g AND key = $k";
			cmd.Parameters.AddWithValue("$g", (long)guildId);
			cmd.Parameters.AddWithValue("$k", key);
			return cmd.ExecuteScalar() as string;
		}
	}

	// ---------------------------------------------------------------- retroactive threading

	private const string RetroSessionColumns =
		"session_id, guild_id, channel_id, anchor_id, invoker_id, thread_name, thread_id, header_message_id, confirm_message_id, status, standalone, anchor_copy_ids, expires_at";
	private const string RetroItemColumns =
		"session_id, original_id, position, author_id, author_name, included, copy_id, verdict, reply_header, extra_copy_ids";
	private const string ActiveStatuses = "('drafting','staging','reviewing','confirming','finalizing')";

	private static RetroSession ReadRetroSession(SqliteDataReader r) => new(
		(ulong)r.GetInt64(0), (ulong)r.GetInt64(1), (ulong)r.GetInt64(2), (ulong)r.GetInt64(3), (ulong)r.GetInt64(4),
		r.GetString(5), (ulong)r.GetInt64(6), (ulong)r.GetInt64(7), (ulong)r.GetInt64(8), r.GetString(9),
		r.GetInt64(10) != 0, r.GetString(11), DateTimeOffset.Parse(r.GetString(12)));

	private static RetroItem ReadRetroItem(SqliteDataReader r) => new(
		(ulong)r.GetInt64(0), (ulong)r.GetInt64(1), r.GetInt32(2), (ulong)r.GetInt64(3), r.GetString(4),
		r.GetInt64(5) != 0, (ulong)r.GetInt64(6), r.GetString(7), r.GetString(8), r.GetString(9));

	private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string, object)[] args)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = sql;
			foreach ((string name, object value) in args)
				cmd.Parameters.AddWithValue(name, value);
			using SqliteDataReader reader = cmd.ExecuteReader();
			List<T> result = [];
			while (reader.Read())
				result.Add(read(reader));
			return result;
		}
	}

	public void UpsertRetroSession(RetroSession s) => Execute(
		$"""
		INSERT INTO retro_sessions ({RetroSessionColumns})
		VALUES ($sid, $gid, $cid, $aid, $iid, $name, $tid, $hid, $fid, $status, $alone, $acid, $exp)
		ON CONFLICT(session_id) DO UPDATE SET anchor_id = $aid, thread_name = $name, thread_id = $tid,
			header_message_id = $hid, confirm_message_id = $fid, status = $status, standalone = $alone,
			anchor_copy_ids = $acid, expires_at = $exp
		""", SessionArgs(s));

	private static (string, object)[] SessionArgs(RetroSession s) =>
	[
		("$sid", (long)s.SessionId), ("$gid", (long)s.GuildId), ("$cid", (long)s.ChannelId), ("$aid", (long)s.AnchorId),
		("$iid", (long)s.InvokerId), ("$name", s.ThreadName), ("$tid", (long)s.ThreadId), ("$hid", (long)s.HeaderMessageId),
		("$fid", (long)s.ConfirmMessageId), ("$status", s.Status), ("$alone", s.Standalone ? 1 : 0),
		("$acid", s.AnchorCopyIds), ("$exp", s.ExpiresAt.ToString("O")),
	];

	/// <summary>
	/// Compare-and-set: writes <paramref name="s"/> only if the stored status is still
	/// <paramref name="expectedStatus"/>. Returns false (writing nothing) when a cancel or
	/// another transition got there first — so a stale snapshot can never resurrect a
	/// cancelled session.
	/// </summary>
	public bool UpdateRetroSessionIf(RetroSession s, string expectedStatus)
	{
		lock (gate)
		{
			using SqliteCommand cmd = connection.CreateCommand();
			cmd.CommandText = """
				UPDATE retro_sessions SET anchor_id = $aid, thread_name = $name, thread_id = $tid,
					header_message_id = $hid, confirm_message_id = $fid, status = $status, standalone = $alone,
					anchor_copy_ids = $acid, expires_at = $exp
				WHERE session_id = $sid AND status = $expected
				""";
			foreach ((string name, object value) in SessionArgs(s))
				cmd.Parameters.AddWithValue(name, value);
			cmd.Parameters.AddWithValue("$expected", expectedStatus);
			return cmd.ExecuteNonQuery() == 1;
		}
	}

	public RetroSession? GetRetroSession(ulong sessionId) => Query(
		$"SELECT {RetroSessionColumns} FROM retro_sessions WHERE session_id = $sid", ReadRetroSession,
		("$sid", (long)sessionId)).FirstOrDefault();

	public List<RetroSession> GetActiveRetroSessions() => Query(
		$"SELECT {RetroSessionColumns} FROM retro_sessions WHERE status IN {ActiveStatuses}", ReadRetroSession);

	/// <summary>Ends a session for good: records its final status and drops its item rows.</summary>
	public void CloseRetroSession(ulong sessionId, string finalStatus)
	{
		Execute("UPDATE retro_sessions SET status = $status WHERE session_id = $sid", ("$status", finalStatus), ("$sid", (long)sessionId));
		Execute("DELETE FROM retro_items WHERE session_id = $sid", ("$sid", (long)sessionId));
	}

	/// <summary>Replaces a session's candidate list in one transaction.</summary>
	public void ReplaceRetroItems(ulong sessionId, IReadOnlyList<RetroItem> items)
	{
		lock (gate)
		{
			using SqliteTransaction tx = connection.BeginTransaction();
			using (SqliteCommand clear = connection.CreateCommand())
			{
				clear.Transaction = tx;
				clear.CommandText = "DELETE FROM retro_items WHERE session_id = $sid";
				clear.Parameters.AddWithValue("$sid", (long)sessionId);
				clear.ExecuteNonQuery();
			}
			foreach (RetroItem item in items)
			{
				using SqliteCommand cmd = connection.CreateCommand();
				cmd.Transaction = tx;
				cmd.CommandText = $"INSERT INTO retro_items ({RetroItemColumns}) VALUES ($sid, $oid, $pos, $aid, $aname, $inc, $cid, $verdict, $hdr, $extra)";
				cmd.Parameters.AddWithValue("$sid", (long)item.SessionId);
				cmd.Parameters.AddWithValue("$oid", (long)item.OriginalId);
				cmd.Parameters.AddWithValue("$pos", item.Position);
				cmd.Parameters.AddWithValue("$aid", (long)item.AuthorId);
				cmd.Parameters.AddWithValue("$aname", item.AuthorName);
				cmd.Parameters.AddWithValue("$inc", item.Included ? 1 : 0);
				cmd.Parameters.AddWithValue("$cid", (long)item.CopyId);
				cmd.Parameters.AddWithValue("$verdict", item.Verdict);
				cmd.Parameters.AddWithValue("$hdr", item.ReplyHeader);
				cmd.Parameters.AddWithValue("$extra", item.ExtraCopyIds);
				cmd.ExecuteNonQuery();
			}
			tx.Commit();
		}
	}

	public List<RetroItem> GetRetroItems(ulong sessionId) => Query(
		$"SELECT {RetroItemColumns} FROM retro_items WHERE session_id = $sid ORDER BY position", ReadRetroItem,
		("$sid", (long)sessionId));

	public void SetRetroCopy(ulong sessionId, ulong originalId, ulong copyId, IEnumerable<ulong> extraCopyIds, string replyHeader) => Execute(
		"UPDATE retro_items SET copy_id = $cid, extra_copy_ids = $extra, reply_header = $hdr WHERE session_id = $sid AND original_id = $oid",
		("$cid", (long)copyId), ("$extra", string.Join(",", extraCopyIds)), ("$hdr", replyHeader),
		("$sid", (long)sessionId), ("$oid", (long)originalId));

	public RetroItem? GetRetroItem(ulong sessionId, ulong originalId) => Query(
		$"SELECT {RetroItemColumns} FROM retro_items WHERE session_id = $sid AND original_id = $oid", ReadRetroItem,
		("$sid", (long)sessionId), ("$oid", (long)originalId)).FirstOrDefault();

	public void SetRetroVerdict(ulong sessionId, ulong originalId, string verdict) => Execute(
		"UPDATE retro_items SET verdict = $verdict WHERE session_id = $sid AND original_id = $oid",
		("$verdict", verdict), ("$sid", (long)sessionId), ("$oid", (long)originalId));

	/// <summary>The item whose staged copy is <paramref name="copyId"/>, if any.</summary>
	public RetroItem? FindRetroItemByCopy(ulong copyId) => copyId == 0 ? null : Query(
		$"SELECT {RetroItemColumns} FROM retro_items WHERE copy_id = $cid", ReadRetroItem,
		("$cid", (long)copyId)).FirstOrDefault();

	/// <summary>Items for <paramref name="originalId"/> in any session (callers filter for active ones).</summary>
	public List<RetroItem> FindRetroItemsByOriginal(ulong originalId) => Query(
		$"SELECT {RetroItemColumns} FROM retro_items WHERE original_id = $oid", ReadRetroItem,
		("$oid", (long)originalId));

	/// <summary>Of the given message IDs, those already claimed by another active session.</summary>
	public HashSet<ulong> ClaimedByActiveRetroSessions(IEnumerable<ulong> originalIds, ulong exceptSessionId)
	{
		HashSet<ulong> wanted = [.. originalIds];
		if (wanted.Count == 0)
			return [];
		List<ulong> claimed = Query(
			$"""
			SELECT i.original_id FROM retro_items i JOIN retro_sessions s ON s.session_id = i.session_id
			WHERE s.status IN {ActiveStatuses} AND s.session_id != $sid AND i.included = 1
			""",
			r => (ulong)r.GetInt64(0), ("$sid", (long)exceptSessionId));
		return [.. claimed.Where(wanted.Contains)];
	}

	public void Dispose() => connection.Dispose();
}
