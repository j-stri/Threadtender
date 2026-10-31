using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;

namespace ThreadTender;

/// <summary>
/// The reusable half of moving a conversation into a thread: copy one message in via
/// webhook impersonation (presentation stripped, reply link recreated), echo its
/// reactions, and ping the moved authors. Deleting originals is deliberately NOT here —
/// the flex sweep deletes each original right after its copy lands, while retroactive
/// threading defers every delete until a human confirms the result.
/// </summary>
public class ThreadImporter(BotConfig config, Transposer transposer, string dataDir)
{
	/// <summary>
	/// The message a copy should link back to: a real reply's reference, or a
	/// pseudo-reply's header target (pseudo-replies carry no Discord reference).
	/// </summary>
	public static ulong? ParentOf(DiscordMessage original) =>
		Transposer.ParseReplyHeaderTarget(original.Content) ?? original.Reference?.Message?.Id;

	/// <summary>
	/// The text a copy is built from: the original's content minus in-channel
	/// presentation (pseudo-reply header, bot-relay "@user:" tag, debug masquerade
	/// prefix) — the repost re-adds its own presentation.
	/// </summary>
	public string? StrippedContent(DiscordMessage original, ulong authorId, string authorName)
	{
		string? content = Transposer.StripReplyHeader(original.Content);
		content = Transposer.StripAuthorTag(content, authorId, authorName);
		if (original.Author is not null && config.IsDebugUser(original.Channel?.GuildId ?? 0, original.Author.Id))
			content = Masquerade.StripPrefix(content);
		return content;
	}

	/// <summary>
	/// Posts a webhook copy of <paramref name="original"/> into <paramref name="thread"/>
	/// as its effective author, optionally headed by a reply link. Returns the copy's
	/// first message. <paramref name="authorName"/> is "" for real users, the synthetic
	/// name for debug identities.
	/// </summary>
	public async Task<DiscordMessage> CopyAsync(DiscordClient client, DiscordThreadChannel thread, DiscordMessage original,
		ulong authorId, string authorName, string? replyHeader, bool recordMapping, List<ulong>? extraPartIds = null)
	{
		ulong guildId = original.Channel?.GuildId ?? thread.GuildId ?? 0;
		string tempDir = Path.Combine(dataDir, "temp", original.Id.ToString());
		MovableContent content = await MovableContent.CaptureAsync(original, tempDir, config.MaxAttachmentBytes(guildId),
			authorId, authorName.Length > 0 ? authorName : null, StrippedContent(original, authorId, authorName));
		try
		{
			return await transposer.RepostAsync(client, thread, content, original.Id, RepostStyle.Webhook, replyHeader, recordMapping, extraPartIds);
		}
		finally
		{
			content.DeleteFiles();
			if (Directory.Exists(tempDir))
			{
				try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
			}
		}
	}

	/// <summary>
	/// Reactions can't be transposed, but the bot can echo each emoji onto the copy — an
	/// invitation for people to re-react. Decoration only: a failed echo never aborts
	/// the move, but it IS logged rather than swallowed. Reads <paramref name="original"/>
	/// as given, so callers must fetch it with skipCache (cached copies predate reactions).
	/// </summary>
	public static async Task EchoReactionsAsync(DiscordMessage original, DiscordMessage copy)
	{
		if (original.Reactions is not { Count: > 0 })
			return;

		// An emoji people already put on the copy itself (e.g. during a retro review) is
		// left alone — the bot adding one more would only inflate that real count.
		List<DiscordEmoji> missing = [.. original.Reactions
			.Select(r => r.Emoji)
			.Where(e => copy.Reactions?.Any(c => c.Count > 0 && SameEmoji(c.Emoji, e)) != true)];
		if (missing.Count == 0)
			return;

		Console.WriteLine(
			$"[ThreadTender] Echoing {missing.Count} reaction(s) from {original.Id}: " +
			string.Join(" ", missing.Select(e => e.ToString())));
		foreach (DiscordEmoji emoji in missing)
		{
			try
			{
				if (!await ReactAsync(copy, emoji))
					Console.WriteLine($"[ThreadTender] Reaction echo {emoji} on copy {copy.Id} gave up after repeated rate limits.");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"[ThreadTender] Reaction echo {emoji} failed on copy {copy.Id}: {ex.GetType().Name}: {ex.Message}");
			}
			// Discord's reaction rate limit is ~1 per 300ms per channel and uses its own
			// bucket; explicit spacing keeps echoes from tripping 429s in the first place.
			await Task.Delay(325);
		}
	}

	/// <summary>
	/// Adds the bot's reaction, waiting out and retrying 429s: on the reactions route the
	/// library surfaces a rate limit as RateLimitException instead of queueing the call.
	/// Returns false only if every attempt was rate limited.
	/// </summary>
	public static async Task<bool> ReactAsync(DiscordMessage message, DiscordEmoji emoji)
	{
		for (int attempt = 1; attempt <= 5; attempt++)
		{
			try
			{
				await message.CreateReactionAsync(emoji);
				return true;
			}
			catch (RateLimitException ex)
			{
				TimeSpan wait = ex.RetryAfter is { } after && after > TimeSpan.Zero ? after : TimeSpan.FromSeconds(attempt);
				await Task.Delay(wait + TimeSpan.FromMilliseconds(250));
			}
		}
		return false;
	}

	public static bool SameEmoji(DiscordEmoji a, DiscordEmoji b) =>
		a.Id != 0 || b.Id != 0 ? a.Id == b.Id : a.Name == b.Name;

	/// <summary>One mass ping so everyone whose messages moved knows where the conversation went.</summary>
	public static async Task PingMovedAuthorsAsync(DiscordThreadChannel thread, IReadOnlyCollection<(ulong Id, string? Name)> authors)
	{
		if (authors.Count == 0)
			return;
		// Synthetic debug identities render as plain bold text; only real users are pinged.
		string tags = string.Join(" ", authors.Select(a => a.Name is not null ? $"**{a.Name}**" : $"<@{a.Id}>"));
		await thread.SendMessageAsync(new DiscordMessageBuilder()
			.WithContent($"{tags} — created discussion thread")
			.WithAllowedMentions(authors.Where(a => a.Name is null).Select(a => (IMention)new UserMention(a.Id)).ToList()));
	}

	/// <summary>Fetches a message bypassing the cache, or null if it no longer exists.</summary>
	public static async Task<DiscordMessage?> FetchFreshAsync(DiscordChannel channel, ulong messageId)
	{
		try { return await channel.GetMessageAsync(messageId, skipCache: true); }
		catch (NotFoundException) { return null; }
	}
}
