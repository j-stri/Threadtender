using System.Collections.Concurrent;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Exceptions;

namespace ThreadTender;

/// <summary>
/// Retroactive threading: pull a conversation that already happened in an UNMANAGED
/// channel into a new thread. Flow — pick a name and anchor, untick strays, stage
/// webhook copies into a review thread, let reviewers ❌ unwanted copies, then Finalize,
/// which is the ONLY step that deletes anything from the channel. Cancel or expiry
/// deletes the staging thread and leaves the channel exactly as it was.
///
/// Invariants: an original is deleted only by Finalize, and only once its complete copy
/// is confirmed to exist; a copy whose original its author deleted never survives; every
/// step resumes safely after a restart.
///
/// Custom IDs (prefix ttr): name (modal), anchor / older (anchor pager), pick (untick
/// menus), stage, fin, cxl. Wizard state lives in retro_sessions / retro_items rather
/// than in the IDs: a thread name can be as long as a custom ID itself.
/// </summary>
public class RetroThreader(BotConfig config, Database db, ThreadImporter importer, Transposer transposer)
{
	public const string ContextMenuName = "Thread from here";
	private const string SlashName = "thread";
	private const int MaxItems = 100, PageSize = 25, AnchorFetch = 60;
	private static readonly TimeSpan DraftLife = TimeSpan.FromMinutes(15); // = interaction token life
	private const string KeepEmoji = "✅", DropEmoji = "❌";

	private readonly ConcurrentDictionary<ulong, SemaphoreSlim> sessionLocks = new();
	private SemaphoreSlim LockFor(ulong sessionId) => sessionLocks.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));

	private bool maintenanceStarted;

	// Finalize retries back off (1, 2, 4 … 60 minutes) so a persistent failure — a lost
	// permission, say — doesn't hammer Discord every minute forever.
	private readonly ConcurrentDictionary<ulong, (int Failures, DateTimeOffset NextTry)> finalizeBackoff = new();

	/// <summary>The commands this feature registers (appended to the guild's bulk overwrite). Open to everyone.</summary>
	public static IEnumerable<DiscordApplicationCommand> Commands() =>
	[
		new(SlashName, "Pull a conversation that already happened into a new thread.", allowDMUsage: false),
		new(ContextMenuName, "", type: DiscordApplicationCommandType.MessageContextMenu, allowDMUsage: false),
	];

	// ================================================================ entry points

	public async Task HandleInteractionAsync(DiscordClient client, InteractionCreatedEventArgs e)
	{
		DiscordInteraction interaction = e.Interaction;
		if (interaction.Type != DiscordInteractionType.ApplicationCommand)
			return;
		string name = interaction.Data.Name;
		if (name != SlashName && name != ContextMenuName)
			return;

		Console.WriteLine($"[ThreadTender] Retro: {name} from {interaction.User.Username}.");

		string? refusal = await RefusalAsync(client, interaction);
		if (refusal is not null)
		{
			await RespondEphemeralAsync(interaction, refusal);
			return;
		}

		ulong anchorId = 0;
		string prefill = "";
		if (name == ContextMenuName)
		{
			// A message context menu's resolved data holds exactly its target message
			// (the library keeps the raw target ID internal).
			DiscordMessage? anchor = interaction.Data.Resolved?.Messages?.Values.FirstOrDefault();
			if (anchor is null || !IsHumanMessage(anchor))
			{
				await RespondEphemeralAsync(interaction, "Pick a message a person wrote — bot and system messages can't anchor a thread.");
				return;
			}
			anchorId = anchor.Id;
			prefill = Snippet(anchor.Content, 80);
		}

		DiscordModalBuilder modal = new DiscordModalBuilder()
			.WithTitle("Name the new thread")
			.WithCustomId($"ttr:name:{anchorId}")
			.AddTextInput(
				new DiscordTextInputComponent("name", "What is this discussion about?", prefill.Length > 0 ? prefill : null, required: true, max_length: 100),
				"Thread name", "Up to 100 characters.");
		await interaction.CreateResponseAsync(DiscordInteractionResponseType.Modal, modal);
	}

	public async Task HandleModalAsync(DiscordClient client, ModalSubmittedEventArgs e)
	{
		string[] parts = e.Id.Split(':');
		if (parts is not ["ttr", "name", var anchorText])
			return;

		string threadName = e.Values.TryGetValue("name", out IModalSubmission? submission) && submission is TextInputModalSubmission text
			? (text.Value ?? "").Trim()
			: "";
		if (threadName.Length == 0)
		{
			await RespondEphemeralAsync(e.Interaction, "The thread needs a name — run the command again.");
			return;
		}

		// Everything below fetches messages; defer before the 3-second ack window closes.
		await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredChannelMessageWithSource,
			new DiscordInteractionResponseBuilder().AsEphemeral());

		string? refusal = await RefusalAsync(client, e.Interaction);
		if (refusal is not null)
		{
			await EditOriginalAsync(e.Interaction, refusal);
			return;
		}

		RetroSession session = new(e.Interaction.Id, e.Interaction.Guild!.Id, e.Interaction.ChannelId, ulong.Parse(anchorText),
			e.Interaction.User.Id, threadName[..Math.Min(threadName.Length, 100)], 0, 0, 0, "drafting", false, "",
			DateTimeOffset.UtcNow + DraftLife);
		db.UpsertRetroSession(session);

		DiscordChannel channel = await client.GetChannelAsync(session.ChannelId);
		DiscordWebhookBuilder page = session.AnchorId == 0
			? await BuildAnchorPageAsync(channel, session, before: null)
			: await BuildUntickAsync(channel, session);
		await e.Interaction.EditOriginalResponseAsync(page);
	}

	public async Task HandleComponentAsync(DiscordClient client, ComponentInteractionCreatedEventArgs e)
	{
		string[] parts = e.Id.Split(':');
		if (parts.Length < 3 || parts[0] != "ttr" || !ulong.TryParse(parts[2], out ulong sessionId))
			return;

		RetroSession? session = db.GetRetroSession(sessionId);
		if (session is null || !session.IsActive)
		{
			await RespondEphemeralAsync(e.Interaction, "This import has already finished or was cancelled.");
			return;
		}

		switch (parts[1])
		{
			case "older" when parts.Length == 4:
			{
				await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredMessageUpdate);
				if (session.Status != "drafting")
					return;
				DiscordChannel channel = await client.GetChannelAsync(session.ChannelId);
				await e.Interaction.EditOriginalResponseAsync(await BuildAnchorPageAsync(channel, session, ulong.Parse(parts[3])));
				return;
			}

			case "anchor":
			{
				await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredMessageUpdate);
				RetroSession? updated = null;
				await WithLockAsync(sessionId, () =>
				{
					// Fresh read under the lock, then compare-and-set: a cancel or expiry
					// that landed during the ack round-trip wins.
					if (db.GetRetroSession(sessionId) is { Status: "drafting" } current
						&& db.UpdateRetroSessionIf(current with { AnchorId = ulong.Parse(e.Values[0]) }, "drafting"))
						updated = db.GetRetroSession(sessionId);
					return Task.CompletedTask;
				});
				if (updated is null)
					return;
				DiscordChannel channel = await client.GetChannelAsync(updated.ChannelId);
				await e.Interaction.EditOriginalResponseAsync(await BuildUntickAsync(channel, updated));
				return;
			}

			case "pick" when parts.Length == 4:
			{
				// Each menu reports its own full selection; store it for Stage to read.
				// No visible update: the client keeps the user's ticks on its own.
				await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredMessageUpdate);
				int page = int.Parse(parts[3]);
				HashSet<ulong> ticked = [.. e.Values.Select(ulong.Parse)];
				await WithLockAsync(sessionId, () =>
				{
					// Under the lock: a late pick racing Stage must not change items that
					// staging has already started copying.
					if (db.GetRetroSession(sessionId) is { Status: "drafting" })
						foreach (RetroItem item in db.GetRetroItems(sessionId).Skip(page * PageSize).Take(PageSize))
							db.SetRetroIncluded(sessionId, item.OriginalId, ticked.Contains(item.OriginalId));
					return Task.CompletedTask;
				});
				return;
			}

			case "stage":
				await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredMessageUpdate);
				await StageFromWizardAsync(client, e.Interaction, sessionId);
				return;

			case "fin":
			{
				await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredChannelMessageWithSource,
					new DiscordInteractionResponseBuilder().AsEphemeral());
				string result = await FinalizeFromButtonAsync(client, sessionId);
				// Finalize may delete the thread this button lived in, or outlast the token.
				try { await EditOriginalAsync(e.Interaction, result); }
				catch (Exception) { }
				return;
			}

			case "cxl":
				if (session.Status == "drafting")
				{
					await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredMessageUpdate);
					await CancelSessionAsync(client, sessionId, reason: null);
					await e.Interaction.EditOriginalResponseAsync(new DiscordWebhookBuilder().WithContent("Cancelled — nothing was changed."));
				}
				else if (session.Status == "finalizing")
				{
					// Don't sit behind Finalize's lock just to say no.
					await RespondEphemeralAsync(e.Interaction, "This import is already being finalized, so it can't be cancelled.");
				}
				else
				{
					await e.Interaction.CreateResponseAsync(DiscordInteractionResponseType.DeferredChannelMessageWithSource,
						new DiscordInteractionResponseBuilder().AsEphemeral());
					bool cancelled = await CancelSessionAsync(client, sessionId, reason: null);
					// The thread this button lived in is gone now; the ephemeral reply may be too.
					try { await EditOriginalAsync(e.Interaction, cancelled ? "Import cancelled — the channel is untouched." : "This import is already being finalized."); }
					catch (Exception) { }
				}
				return;
		}
	}

	// ================================================================ gating

	/// <summary>Why this interaction can't start an import here, or null if it can.</summary>
	private async Task<string?> RefusalAsync(DiscordClient client, DiscordInteraction interaction)
	{
		if (interaction.Guild is null)
			return "This only works in a server.";
		if (!config.RetroEnabled(interaction.Guild.Id))
			return "Retroactive threading is switched off on this server.";

		DiscordChannel channel = await client.GetChannelAsync(interaction.ChannelId);
		if (channel is DiscordThreadChannel)
			return "Run this in the channel itself, not inside a thread.";
		if (channel.Type is not (DiscordChannelType.Text or DiscordChannelType.News))
			return "This only works in text channels.";
		if (config.IsManagedChannel(channel.Id))
			return "This channel is bound to ThreadTender, so its binding already decides where discussion goes.";

		// AppPermissions = the bot's effective permissions in this channel.
		DiscordPermissions perms = interaction.AppPermissions;
		(DiscordPermission Permission, string Name)[] needed =
		[
			(DiscordPermission.ViewChannel, "View Channel"),
			(DiscordPermission.ReadMessageHistory, "Read Message History"),
			(DiscordPermission.ManageMessages, "Manage Messages"),
			(DiscordPermission.ManageWebhooks, "Manage Webhooks"),
			(DiscordPermission.CreatePublicThreads, "Create Public Threads"),
			(DiscordPermission.SendThreadMessages, "Send Messages in Threads"),
			(DiscordPermission.ManageThreads, "Manage Threads"),
			(DiscordPermission.AddReactions, "Add Reactions"),
		];
		string[] missing = needed.Where(n => !perms.HasPermission(n.Permission)).Select(n => n.Name).ToArray();
		return missing.Length > 0
			? $"I'm missing permissions in <#{channel.Id}>: {string.Join(", ", missing)}."
			: null;
	}

	private static bool IsHumanMessage(DiscordMessage m) =>
		m.Author is { IsBot: false } && m.WebhookId is null
		&& m.MessageType is DiscordMessageType.Default or DiscordMessageType.Reply;

	// ================================================================ wizard

	private static async Task<DiscordWebhookBuilder> BuildAnchorPageAsync(DiscordChannel channel, RetroSession session, ulong? before)
	{
		List<DiscordMessage> raw = [];
		IAsyncEnumerable<DiscordMessage> source = before is { } b
			? channel.GetMessagesBeforeAsync(b, AnchorFetch)
			: channel.GetMessagesAsync(AnchorFetch);
		await foreach (DiscordMessage m in source)
			raw.Add(m);
		raw = [.. raw.OrderByDescending(m => m.Id)]; // newest first, whatever order the API used

		List<DiscordMessage> humans = [.. raw.Where(IsHumanMessage).Take(PageSize)];
		DiscordWebhookBuilder builder = new();

		if (humans.Count == 0)
		{
			return builder
				.WithContent(before is null ? "There are no recent messages here to anchor a thread on." : "No older messages to pick from.")
				.AddActionRowComponent(new DiscordButtonComponent(DiscordButtonStyle.Secondary, $"ttr:cxl:{session.SessionId}", "Cancel"));
		}

		builder.WithContent($"**{session.ThreadName}** — which message starts the conversation? Everything after it is offered next.")
			.AddActionRowComponent(new DiscordSelectComponent($"ttr:anchor:{session.SessionId}", "Pick the first message…",
				humans.Select(m => new DiscordSelectComponentOption(Label(m), m.Id.ToString(), $"{m.Timestamp:MMM d, HH:mm} UTC")).ToList()));

		// Page past everything fetched, humans or not, so bot chatter isn't rescanned.
		bool more = raw.Count >= AnchorFetch;
		builder.AddActionRowComponent(
			new DiscordButtonComponent(DiscordButtonStyle.Secondary, $"ttr:older:{session.SessionId}:{raw[^1].Id}", "Older", disabled: !more),
			new DiscordButtonComponent(DiscordButtonStyle.Secondary, $"ttr:cxl:{session.SessionId}", "Cancel"));
		return builder;
	}

	/// <summary>Loads the (at most 100) human messages after the anchor, stores them as ticked items, and renders the untick menus.</summary>
	private async Task<DiscordWebhookBuilder> BuildUntickAsync(DiscordChannel channel, RetroSession session)
	{
		DiscordMessage? anchor = await ThreadImporter.FetchFreshAsync(channel, session.AnchorId);
		if (anchor is null)
			return new DiscordWebhookBuilder().WithContent("That message no longer exists — run the command again.");

		List<DiscordMessage> after = [];
		await foreach (DiscordMessage m in channel.GetMessagesAfterAsync(session.AnchorId, MaxItems))
			after.Add(m);

		HashSet<ulong> claimed = db.ClaimedByActiveRetroSessions(after.Select(m => m.Id), session.SessionId);
		List<DiscordMessage> candidates = [.. after
			.Where(IsHumanMessage)
			.Where(m => !claimed.Contains(m.Id) && db.GetTransposedThread(m.Id) is null)
			.OrderBy(m => m.Id)
			.Take(MaxItems)];

		// Resolve each message's effective author now (debug masquerades included), so
		// copies and the Finalize ping credit the right person.
		List<RetroItem> items = [];
		foreach ((DiscordMessage m, int position) in candidates.Select((m, i) => (m, i)))
		{
			(EffectiveAuthor author, string? _) = await Masquerade.ResolveAsync(config, m);
			items.Add(new RetroItem(session.SessionId, m.Id, position, author.Id, author.NameOverride ?? "", true, 0, "pending", "", ""));
		}
		db.ReplaceRetroItems(session.SessionId, items);

		DiscordWebhookBuilder builder = new();
		if (items.Count == 0)
		{
			return builder
				.WithContent($"Nothing after {anchor.JumpLink} can be moved — there are no messages from people after it, or they're already part of another import.")
				.AddActionRowComponent(new DiscordButtonComponent(DiscordButtonStyle.Secondary, $"ttr:cxl:{session.SessionId}", "Cancel"));
		}

		string skipped = claimed.Count > 0 ? $" {claimed.Count} message(s) already in another import were left out." : "";
		builder.WithContent(
			$"**{session.ThreadName}**, starting at {anchor.JumpLink}.\n" +
			$"These {items.Count} message(s) will be copied into the thread. Untick anything that doesn't belong, then press **Stage thread**. " +
			$"Nothing is removed from the channel until the thread is reviewed and finalized.{skipped}");

		for (int page = 0; page * PageSize < candidates.Count; page++)
		{
			List<DiscordMessage> chunk = [.. candidates.Skip(page * PageSize).Take(PageSize)];
			builder.AddActionRowComponent(new DiscordSelectComponent(
				$"ttr:pick:{session.SessionId}:{page}",
				$"Messages {page * PageSize + 1}–{page * PageSize + chunk.Count}",
				chunk.Select(m => new DiscordSelectComponentOption(Label(m), m.Id.ToString(), $"{m.Timestamp:MMM d, HH:mm} UTC", isDefault: true)).ToList(),
				disabled: false, minOptions: 0, maxOptions: chunk.Count));
		}

		builder.AddActionRowComponent(
			new DiscordButtonComponent(DiscordButtonStyle.Success, $"ttr:stage:{session.SessionId}", "Stage thread"),
			new DiscordButtonComponent(DiscordButtonStyle.Secondary, $"ttr:cxl:{session.SessionId}", "Cancel"));
		return builder;
	}

	// ================================================================ staging

	private async Task StageFromWizardAsync(DiscordClient client, DiscordInteraction interaction, ulong sessionId)
	{
		string? problem = null;
		bool claimed = false;
		await WithLockAsync(sessionId, () =>
		{
			if (db.GetRetroSession(sessionId) is not { Status: "drafting" } session)
				return Task.CompletedTask; // double-click, expiry, or already staged
			if (!db.GetRetroItems(sessionId).Any(i => i.Included))
			{
				problem = "Everything is unticked, so there's nothing to move. Tick at least one message, or cancel.";
				return Task.CompletedTask;
			}
			claimed = db.UpdateRetroSessionIf(session with
			{
				Status = "staging",
				ExpiresAt = DateTimeOffset.UtcNow.AddHours(config.RetroReviewHours(session.GuildId)),
			}, "drafting");
			return Task.CompletedTask;
		});

		if (problem is not null)
		{
			await interaction.EditOriginalResponseAsync(new DiscordWebhookBuilder().WithContent(problem));
			return;
		}
		if (!claimed)
			return;

		await interaction.EditOriginalResponseAsync(new DiscordWebhookBuilder().WithContent("⏳ Staging the thread — this takes about a minute per 50 messages…"));
		string outcome = await StageOrCancelAsync(client, sessionId) is { } threadId
			? $"Staged in <#{threadId}>. Anyone can review it there; nothing leaves the channel until someone presses **Finalize**."
			: "The import was cancelled — the channel is untouched. (If you didn't cancel it, staging failed: " +
			  "the first message may have been deleted, or I may be missing permissions.)";
		try { await interaction.EditOriginalResponseAsync(new DiscordWebhookBuilder().WithContent(outcome)); }
		catch (Exception) { } // staging can outlive the 15-minute interaction token
	}

	/// <summary>Runs (or resumes) staging; on any failure, cancels so nothing is left half-built. Returns the thread ID, or null if the session didn't make it to review.</summary>
	private async Task<ulong?> StageOrCancelAsync(DiscordClient client, ulong sessionId)
	{
		try
		{
			ulong threadId = await StageAsync(client, sessionId);
			return db.GetRetroSession(sessionId)?.Status is "reviewing" or "confirming" ? threadId : null;
		}
		catch (Exception ex)
		{
			// A cancel mid-staging deletes the thread out from under the copy loop, which
			// lands here too — only log it as a failure if nobody cancelled.
			if (db.GetRetroSession(sessionId)?.Status != "cancelled")
			{
				Console.WriteLine($"[ThreadTender] Retro staging failed for session {sessionId}: {ex}");
				await CancelSessionAsync(client, sessionId, reason: null);
			}
			return null;
		}
	}

	/// <summary>
	/// Creates the review thread (once) and copies every ticked item into it. Resumable:
	/// every step is persisted before the next, items that already have a copy are
	/// skipped, and stray copies from a crash are swept. Every session write is a
	/// compare-and-set on "staging", so a cancel at any moment stops it cold.
	/// </summary>
	private async Task<ulong> StageAsync(DiscordClient client, ulong sessionId)
	{
		RetroSession session = db.GetRetroSession(sessionId) ?? throw new InvalidOperationException($"Session {sessionId} vanished");
		if (session.Status != "staging")
			return session.ThreadId;
		DiscordChannel channel = await client.GetChannelAsync(session.ChannelId);
		bool resuming = session.ThreadId != 0;

		DiscordThreadChannel thread;
		if (!resuming)
		{
			DiscordMessage anchor = await ThreadImporter.FetchFreshAsync(channel, session.AnchorId)
				?? throw new InvalidOperationException("The anchor message was deleted before staging.");
			(thread, bool standalone) = await CreateThreadAsync(client, channel, anchor, session);

			// Persist the thread before posting anything, so a crash can't make a second one.
			if (!db.UpdateRetroSessionIf(session with { ThreadId = thread.Id, Standalone = standalone }, "staging"))
			{
				await DeleteThreadQuietlyAsync(thread); // cancelled while we were creating it
				return thread.Id;
			}
		}
		else
		{
			thread = await client.GetChannelAsync(session.ThreadId) as DiscordThreadChannel
				?? throw new InvalidOperationException($"Thread {session.ThreadId} is not a thread.");
		}

		List<RetroItem> items = db.GetRetroItems(sessionId);
		session = db.GetRetroSession(sessionId)!;

		if (session.HeaderMessageId == 0)
		{
			DiscordMessage header = await thread.SendMessageAsync(BuildHeader(session, items.Count(i => i.Included)));
			if (!db.UpdateRetroSessionIf(session with { HeaderMessageId = header.Id }, "staging"))
				return thread.Id;
			session = db.GetRetroSession(sessionId)!;
		}

		if (session.Standalone && session.AnchorCopyIds.Length == 0)
		{
			// A standalone thread isn't attached to the anchor, so the anchor is copied in
			// as its opening post. It stays in the channel and takes no review controls.
			if (await ThreadImporter.FetchFreshAsync(channel, session.AnchorId) is { } anchor)
			{
				List<ulong> anchorParts = [];
				DiscordMessage anchorCopy = await importer.CopyAsync(client, thread, anchor, anchor.Author!.Id, "", replyHeader: null,
					recordMapping: false, anchorParts);
				string ids = string.Join(",", new[] { anchorCopy.Id }.Concat(anchorParts));
				if (!db.UpdateRetroSessionIf(session with { AnchorCopyIds = ids }, "staging"))
					return thread.Id;
				session = db.GetRetroSession(sessionId)!;
			}
		}

		if (resuming)
			await SweepStrayCopiesAsync(thread, session, items);

		// Original → copy, for recreating reply chains between the moved messages.
		Dictionary<ulong, ulong> copyIds = items.Where(i => i.CopyId != 0).ToDictionary(i => i.OriginalId, i => i.CopyId);

		foreach (RetroItem item in items.Where(i => i.Included))
		{
			if (db.GetRetroSession(sessionId) is not { Status: "staging" })
				return thread.Id; // cancelled (or switched off) mid-staging

			if (item.CopyId != 0)
			{
				// Copied before a restart; make sure its review controls made it on too.
				if (item.Verdict != "drop" && await ThreadImporter.FetchFreshAsync(thread, item.CopyId) is { } existing)
					await SeedControlsAsync(existing);
				continue;
			}
			if (item.Verdict == "drop")
				continue;

			DiscordMessage? original = await ThreadImporter.FetchFreshAsync(channel, item.OriginalId);
			if (original is null)
			{
				db.SetRetroVerdict(sessionId, item.OriginalId, "drop"); // deleted since the wizard listed it
				continue;
			}

			// Replies to the anchor need no marker: the thread hangs off the anchor.
			string? replyHeader = ThreadImporter.ParentOf(original) is { } parent && parent != session.AnchorId
				&& copyIds.TryGetValue(parent, out ulong parentCopy)
				? Transposer.ReplyHeaderFor(session.GuildId, thread.Id, parentCopy)
				: null;

			List<ulong> extraParts = [];
			DiscordMessage copy = await importer.CopyAsync(client, thread, original, item.AuthorId, item.AuthorName, replyHeader,
				recordMapping: false, extraParts);
			db.SetRetroCopy(sessionId, item.OriginalId, copy.Id, extraParts, replyHeader ?? "");
			copyIds[item.OriginalId] = copy.Id;

			// The author may have deleted the original while it was being copied; the
			// delete handler couldn't reach a copy that didn't exist yet, so check now.
			if (db.GetRetroItem(sessionId, item.OriginalId) is { Verdict: "drop" } dropped)
			{
				await DeleteCopyAsync(thread, dropped);
				continue;
			}
			await SeedControlsAsync(copy);
		}

		await WithLockAsync(sessionId, async () =>
		{
			if (db.GetRetroSession(sessionId) is { Status: "staging" } staged
				&& db.UpdateRetroSessionIf(staged with { Status = "reviewing" }, "staging"))
			{
				// Staging is done at this point; a failed confirm post must not be mistaken
				// for a failed staging (which would cancel a complete thread). The next
				// verdict, or recovery, posts it again.
				try { await MaybeConfirmAsync(client, sessionId); }
				catch (Exception ex) { Console.WriteLine($"[ThreadTender] Retro confirm post for {sessionId} failed: {ex.Message}"); }
			}
		});
		return thread.Id;
	}

	/// <summary>
	/// Creates the review thread on the anchor. If the anchor already has a thread, that
	/// is either ours from a crash between creating and recording it (adopted), or
	/// someone else's (kept separate by design: this import gets its own standalone thread).
	/// </summary>
	private static async Task<(DiscordThreadChannel Thread, bool Standalone)> CreateThreadAsync(
		DiscordClient client, DiscordChannel channel, DiscordMessage anchor, RetroSession session)
	{
		try
		{
			return (await channel.CreateThreadAsync(anchor, session.ThreadName, DiscordAutoArchiveDuration.Week, "ThreadTender: retroactive thread"), false);
		}
		catch (BadRequestException original)
		{
			DiscordThreadChannel? existing;
			try { existing = await client.GetChannelAsync(anchor.Id) as DiscordThreadChannel; }
			catch (NotFoundException) { existing = null; }
			if (existing is null)
				System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(original); // a different 400: surface it intact

			// Adopt only a thread that is unmistakably our own crash leftover: made by the
			// bot, named for this session, and still EMPTY (staging records the thread
			// before posting anything into it). An earlier finished import or a flex/pure
			// thread on the same anchor always has messages, so it is never taken over —
			// and therefore never deleted by this session's cancel or stray sweep.
			if (existing!.CreatorId == client.CurrentUser.Id && existing.Name == session.ThreadName)
			{
				bool empty = true;
				await foreach (DiscordMessage _ in existing.GetMessagesAsync(1))
					empty = false;
				if (empty)
					return (existing, false);
			}

			DiscordChannelType threadType = channel.Type == DiscordChannelType.News ? DiscordChannelType.NewsThread : DiscordChannelType.PublicThread;
			return ((DiscordThreadChannel)await channel.CreateThreadAsync(session.ThreadName, DiscordAutoArchiveDuration.Week,
				threadType, "ThreadTender: retroactive thread (anchor already had one)"), true);
		}
	}

	/// <summary>
	/// After a crash between posting a copy and recording it, an unrecorded webhook copy
	/// sits in the thread with no review controls. Delete any webhook message no item knows.
	/// </summary>
	private static async Task SweepStrayCopiesAsync(DiscordThreadChannel thread, RetroSession session, List<RetroItem> items)
	{
		HashSet<ulong> known = [.. items.SelectMany(i => i.AllCopyIds), .. session.AnchorCopyIdList];

		List<DiscordMessage> strays = [];
		await foreach (DiscordMessage m in thread.GetMessagesAsync(MaxItems * 3))
			if (m.WebhookId is not null && !known.Contains(m.Id))
				strays.Add(m);
		foreach (DiscordMessage stray in strays)
		{
			try { await stray.DeleteAsync("ThreadTender: unrecorded copy from an interrupted staging"); }
			catch (NotFoundException) { }
		}
		if (strays.Count > 0)
			Console.WriteLine($"[ThreadTender] Retro: swept {strays.Count} stray copy message(s) from session {session.SessionId}.");
	}

	private DiscordMessageBuilder BuildHeader(RetroSession session, int count) => new DiscordMessageBuilder()
		.WithContent(
			$"**Retroactive thread — review before it's final.** {count} message(s) copied from <#{session.ChannelId}>, " +
			$"starting at https://discord.com/channels/{session.GuildId}/{session.ChannelId}/{session.AnchorId}.\n" +
			$"React {DropEmoji} on a copy to drop it (its original stays in the channel); {KeepEmoji} just marks it as checked. " +
			$"Nothing leaves <#{session.ChannelId}> until someone presses **Finalize**; unreviewed copies count as kept. " +
			$"If nobody finalizes by <t:{session.ExpiresAt.ToUnixTimeSeconds()}:f>, the import is cancelled and this thread deleted.")
		.AddActionRowComponent(
			new DiscordButtonComponent(DiscordButtonStyle.Success, $"ttr:fin:{session.SessionId}", "Finalize now"),
			new DiscordButtonComponent(DiscordButtonStyle.Danger, $"ttr:cxl:{session.SessionId}", "Cancel import"));

	/// <summary>Adds whichever of the bot's ✅/❌ controls are missing (idempotent — used on fresh copies and on resume).</summary>
	private static async Task SeedControlsAsync(DiscordMessage copy)
	{
		foreach (string emoji in new[] { KeepEmoji, DropEmoji })
		{
			if (copy.Reactions?.Any(r => r.IsMe && r.Emoji.Name == emoji) == true)
				continue;
			try { await copy.CreateReactionAsync(DiscordEmoji.FromUnicode(emoji)); }
			catch (NotFoundException) { return; }
			await Task.Delay(325); // reaction rate limit (~1 per 300 ms per channel)
		}
	}

	// ================================================================ review

	public async Task HandleReactionAddedAsync(DiscordClient client, MessageReactionAddedEventArgs e)
	{
		if (e.User.IsBot)
			return;
		string emoji = e.Emoji.Name;
		if (emoji != KeepEmoji && emoji != DropEmoji)
			return;
		if (db.FindRetroItemByCopy(e.Message.Id) is not { } found)
			return;

		await WithLockAsync(found.SessionId, async () =>
		{
			// Verdicts may land while later copies are still being staged; Finalize and
			// Cancel own the session once they start.
			if (db.GetRetroSession(found.SessionId) is not { Status: "staging" or "reviewing" or "confirming" } session)
				return;
			RetroItem? item = db.GetRetroItem(found.SessionId, found.OriginalId);
			if (item is null || item.Verdict == "drop")
				return;

			if (emoji == DropEmoji)
			{
				// ❌ wins outright: the copy goes at once, the original stays in the channel.
				db.SetRetroVerdict(item.SessionId, item.OriginalId, "drop");
				if (await client.GetChannelAsync(session.ThreadId) is DiscordThreadChannel thread)
					await DeleteCopyAsync(thread, item);
			}
			else if (item.Verdict == "pending")
			{
				db.SetRetroVerdict(item.SessionId, item.OriginalId, "keep");
			}
			else
			{
				return;
			}
			await MaybeConfirmAsync(client, item.SessionId);
		});
	}

	/// <summary>
	/// Keeps the review honest when messages vanish: an author deleting their original
	/// takes its copy with it (the copy would otherwise resurrect words they removed), a
	/// copy deleted by hand counts as dropped, and a deleted anchor takes its standalone copy.
	/// </summary>
	public async Task HandleMessageDeletedAsync(DiscordClient client, MessageDeletedEventArgs e)
	{
		ulong id = e.Message.Id;

		if (db.FindRetroItemByCopy(id) is { } byCopy)
			await DropIfReviewingAsync(client, byCopy, deleteCopy: false);

		foreach (RetroItem byOriginal in db.FindRetroItemsByOriginal(id))
			await DropIfReviewingAsync(client, byOriginal, deleteCopy: true);

		foreach (RetroSession session in db.GetActiveRetroSessions().Where(s => s.AnchorId == id && s.AnchorCopyIds.Length > 0))
		{
			try
			{
				if (await client.GetChannelAsync(session.ThreadId) is DiscordThreadChannel thread)
					await DeleteMessagesQuietlyAsync(thread, session.AnchorCopyIdList, "ThreadTender: the anchor was deleted by its author");
			}
			catch (NotFoundException) { }
		}
	}

	private async Task DropIfReviewingAsync(DiscordClient client, RetroItem item, bool deleteCopy)
	{
		await WithLockAsync(item.SessionId, async () =>
		{
			// While finalizing, Finalize itself handles vanished originals (and its own
			// deletes fire this event too), so leave it alone.
			if (db.GetRetroSession(item.SessionId) is not { Status: "staging" or "reviewing" or "confirming" } session)
				return;
			RetroItem? current = db.GetRetroItem(item.SessionId, item.OriginalId);
			if (current is null || current.Verdict == "drop")
				return;

			db.SetRetroVerdict(item.SessionId, item.OriginalId, "drop");
			if (deleteCopy && current.CopyId != 0 && session.ThreadId != 0
				&& await client.GetChannelAsync(session.ThreadId) is DiscordThreadChannel thread)
				await DeleteCopyAsync(thread, current);
			await MaybeConfirmAsync(client, item.SessionId);
		});
	}

	/// <summary>
	/// Once every staged copy has a verdict, posts the "does this look good?" message;
	/// if it's already up, refreshes its counts. Caller holds the session lock.
	/// </summary>
	private async Task MaybeConfirmAsync(DiscordClient client, ulong sessionId)
	{
		if (db.GetRetroSession(sessionId) is not { Status: "reviewing" or "confirming" } session)
			return;
		List<RetroItem> staged = [.. db.GetRetroItems(sessionId).Where(i => i.Included && (i.CopyId != 0 || i.Verdict == "drop"))];
		if (staged.Any(i => i.Verdict == "pending"))
			return;

		int kept = staged.Count(i => i.Verdict == "keep"), dropped = staged.Count(i => i.Verdict == "drop");
		string text = kept > 0
			? $"All {staged.Count} copies reviewed: **{kept}** kept, **{dropped}** dropped. **Does this look good?** " +
			  $"Finalize deletes the {kept} kept original(s) from <#{session.ChannelId}> and makes this thread permanent."
			: "Every copy was dropped, so there's nothing to move. Cancel to delete this thread (Finalize does the same).";

		if (await client.GetChannelAsync(session.ThreadId) is not DiscordThreadChannel thread)
			return;

		if (session.Status == "confirming" && session.ConfirmMessageId != 0
			&& await ThreadImporter.FetchFreshAsync(thread, session.ConfirmMessageId) is { } existing)
		{
			await existing.ModifyAsync(text);
			return;
		}

		DiscordMessage confirm = await thread.SendMessageAsync(new DiscordMessageBuilder()
			.WithContent(text)
			.AddActionRowComponent(
				new DiscordButtonComponent(DiscordButtonStyle.Success, $"ttr:fin:{sessionId}", "Finalize"),
				new DiscordButtonComponent(DiscordButtonStyle.Danger, $"ttr:cxl:{sessionId}", "Cancel")));
		if (!db.UpdateRetroSessionIf(session with { Status = "confirming", ConfirmMessageId = confirm.Id }, session.Status))
		{
			try { await confirm.DeleteAsync(); } catch (NotFoundException) { }
		}
	}

	// ================================================================ finalize

	private async Task<string> FinalizeFromButtonAsync(DiscordClient client, ulong sessionId)
	{
		string result = "This import has already finished or was cancelled.";
		await WithLockAsync(sessionId, async () =>
		{
			RetroSession? session = db.GetRetroSession(sessionId);
			switch (session?.Status)
			{
				case "staging":
					result = "Still staging — wait until every copy is posted, then finalize.";
					return;
				case "reviewing" or "confirming" or "finalizing": // finalizing = resuming one that hit an error
					break;
				default:
					return;
			}

			// Persist the claim BEFORE deleting anything: a restart resumes a finalize
			// rather than reopening a half-applied review.
			if (session.Status != "finalizing" && !db.UpdateRetroSessionIf(session with { Status = "finalizing" }, session.Status))
				return;
			result = await TryFinalizeAsync(client, sessionId)
				?? "Finalizing hit an error partway through. Press **Finalize** again to resume — it picks up where it stopped " +
				   "(and I'll keep retrying on my own every minute).";
		});
		return result;
	}

	/// <summary>Runs Finalize, logging instead of throwing. Null = failed partway (safe to rerun). Caller holds the lock.</summary>
	private async Task<string?> TryFinalizeAsync(DiscordClient client, ulong sessionId)
	{
		try
		{
			return await FinalizeAsync(client, sessionId);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[ThreadTender] Retro finalize of {sessionId} failed partway: {ex}");
			return null;
		}
	}

	/// <summary>
	/// Makes the import permanent. Caller holds the session lock and has set "finalizing".
	///
	/// Phase A settles every copy against its original: a copy someone ❌'d while the bot
	/// wasn't listening is dropped; an original its author deleted takes its copy along; an
	/// original edited during review gets its copy updated, or, if the edit can't be carried
	/// across, the stale copy is dropped and the original kept; a copy that couldn't carry
	/// every attachment keeps its original in the channel ("stay"). Review controls are
	/// swapped for an echo of the original's reactions.
	///
	/// Phase B takes a last look at each survivor from fresh data (every chunk of the copy
	/// exists; the original is still there and hasn't been edited since its copy last
	/// matched), and only the items that pass are marked "moved" — immediately before their
	/// originals are deleted. So on resume, a missing original already marked "moved" is one
	/// WE deleted; any other missing original was deleted by its author, and its copy goes too.
	/// </summary>
	private async Task<string> FinalizeAsync(DiscordClient client, ulong sessionId)
	{
		// The sweep picks sessions from a list read before taking the lock; one a user's
		// Finalize has just completed must not be finalized (or reclassified) again.
		if (db.GetRetroSession(sessionId) is not { Status: "finalizing" } session)
			return "This import has already finished or was cancelled.";

		DiscordChannel channel;
		try { channel = await client.GetChannelAsync(session.ChannelId); }
		catch (NotFoundException)
		{
			// The source channel is gone, and its originals with it: nothing left to delete.
			db.CloseRetroSession(sessionId, "done");
			return "The original channel no longer exists, so the thread stays as it is.";
		}

		DiscordThreadChannel? thread;
		try { thread = await client.GetChannelAsync(session.ThreadId) as DiscordThreadChannel; }
		catch (NotFoundException) { thread = null; }
		if (thread is null)
		{
			// The review thread was deleted by hand. Without copies nothing can be moved
			// safely, so stop here; anything already deleted had a confirmed copy at the time.
			db.CloseRetroSession(sessionId, "cancelled");
			return "The review thread no longer exists, so nothing more was moved.";
		}

		// Backstop: no copy of a dropped item may outlive the review (a failed ❌ delete,
		// or an original deleted mid-copy).
		foreach (RetroItem item in db.GetRetroItems(sessionId).Where(i => i.Verdict == "drop" && i.CopyId != 0))
			await DeleteCopyAsync(thread, item);

		// A standalone thread opens with a copy of the anchor; if the anchor's author
		// deleted it, that copy goes as well.
		if (session.AnchorCopyIds.Length > 0 && await ThreadImporter.FetchFreshAsync(channel, session.AnchorId) is null)
			await DeleteMessagesQuietlyAsync(thread, session.AnchorCopyIdList, "ThreadTender: the anchor was deleted by its author");

		HashSet<ulong> movedBeforeThisRun = [.. db.GetRetroItems(sessionId).Where(i => i.Verdict == "moved").Select(i => i.OriginalId)];
		int editsKept = 0, lossyKept = 0;

		// ---- phase A
		foreach (RetroItem item in db.GetRetroItems(sessionId).Where(i => i.Included && i.CopyId != 0 && i.Verdict is "pending" or "keep"))
		{
			DiscordMessage? copy = await ThreadImporter.FetchFreshAsync(thread, item.CopyId);
			if (copy is null)
			{
				db.SetRetroVerdict(sessionId, item.OriginalId, "drop"); // copy deleted by hand: the original stays
				await DeleteCopyAsync(thread, item); // any later chunks with it
				continue;
			}
			if (OthersReacted(copy, DropEmoji))
			{
				// A ❌ the live handler never saw (bot down, gateway dropped the event).
				db.SetRetroVerdict(sessionId, item.OriginalId, "drop");
				await DeleteCopyAsync(thread, item);
				continue;
			}

			DiscordMessage? original = await ThreadImporter.FetchFreshAsync(channel, item.OriginalId);
			if (original is null)
			{
				db.SetRetroVerdict(sessionId, item.OriginalId, "drop");
				await DeleteCopyAsync(thread, item);
				continue;
			}

			if (!await EnsureEditCarriedAsync(client, thread, item, original, copy))
			{
				editsKept++;
				continue;
			}

			try { await copy.DeleteAllReactionsAsync("ThreadTender: review finished"); }
			catch (NotFoundException) { }
			await ThreadImporter.EchoReactionsAsync(original, copy);

			// A copy that couldn't re-upload every attachment links to the original's
			// files instead; deleting the original would kill those links. Recorded only
			// after the reaction swap, so a crash can't leave review controls behind on it.
			if ((copy.Attachments?.Count ?? 0) < (original.Attachments?.Count ?? 0))
			{
				db.SetRetroVerdict(sessionId, item.OriginalId, "stay");
				lossyKept++;
			}
		}

		// ---- phase B: last look, from fresh data, before anything is deleted
		List<RetroItem> candidates = [.. db.GetRetroItems(sessionId).Where(i => i.Included && i.CopyId != 0 && i.Verdict is "keep" or "pending" or "moved")];
		List<RetroItem> confirmed = [];
		List<DiscordMessage> originals = [];
		List<(ulong Id, string? Name)> authors = [];
		foreach (RetroItem item in candidates)
		{
			// The copy may have been deleted during phase A's slow reaction echoes, or
			// while the bot was down; then the original must stay.
			if (!await CopyIntactAsync(thread, item))
			{
				db.SetRetroVerdict(sessionId, item.OriginalId, "drop");
				await DeleteCopyAsync(thread, item);
				continue;
			}

			DiscordMessage? original = await ThreadImporter.FetchFreshAsync(channel, item.OriginalId);
			if (original is null && !movedBeforeThisRun.Contains(item.OriginalId))
			{
				// Its author deleted it during Finalize: the copy goes too.
				db.SetRetroVerdict(sessionId, item.OriginalId, "drop");
				await DeleteCopyAsync(thread, item);
				continue;
			}
			if (original is not null)
			{
				// An edit made after phase A synced the copy would die with the original.
				DiscordMessage? copy = await ThreadImporter.FetchFreshAsync(thread, item.CopyId);
				if (copy is null || !await EnsureEditCarriedAsync(client, thread, item, original, copy))
				{
					if (copy is not null)
						editsKept++;
					continue;
				}
				originals.Add(original);
			}

			confirmed.Add(item);
			if (!authors.Any(a => a.Id == item.AuthorId))
				authors.Add((item.AuthorId, item.AuthorName.Length > 0 ? item.AuthorName : null));
		}

		bool anyStay = db.GetRetroItems(sessionId).Any(i => i.Verdict == "stay");
		if (confirmed.Count == 0 && !anyStay)
		{
			await CancelSessionAsync(client, sessionId, reason: null, force: true);
			return "Every copy was dropped, so the import was cancelled and the thread deleted.";
		}

		// Marked immediately before the delete, so "moved" reliably means "we deleted it".
		foreach (RetroItem item in confirmed)
			db.SetRetroVerdict(sessionId, item.OriginalId, "moved");
		await DeleteOriginalsAsync(channel, originals);

		foreach (ulong reviewMessage in new[] { session.HeaderMessageId, session.ConfirmMessageId }.Where(id => id != 0))
		{
			if (await ThreadImporter.FetchFreshAsync(thread, reviewMessage) is { } message)
			{
				try { await message.DeleteAsync(); } catch (NotFoundException) { }
			}
		}

		await ThreadImporter.PingMovedAuthorsAsync(thread, authors);
		db.CloseRetroSession(sessionId, "done");
		Console.WriteLine($"[ThreadTender] Retro session {sessionId} finalized into {thread.Id}: {confirmed.Count} moved.");

		string result = $"Finalized — the thread is live at <#{thread.Id}>.";
		if (editsKept > 0)
			result += $" {editsKept} message(s) edited during review couldn't be updated in the thread, so they stayed in the channel.";
		if (lossyKept > 0)
			result += $" {lossyKept} message(s) with attachments that couldn't be re-uploaded stayed in the channel too, so their files aren't lost.";
		return result;
	}

	/// <summary>
	/// If the original was edited after its copy last matched it, carries the edit into
	/// the copy. When that isn't possible, drops the stale copy so the original (with its
	/// edit) stays in the channel, and returns false.
	/// </summary>
	private async Task<bool> EnsureEditCarriedAsync(DiscordClient client, DiscordThreadChannel thread, RetroItem item, DiscordMessage original, DiscordMessage copy)
	{
		DateTimeOffset copyMatched = copy.EditedTimestamp ?? copy.CreationTimestamp;
		if (original.EditedTimestamp is not { } edited || edited <= copyMatched)
			return true;
		if (await TrySyncEditAsync(client, thread, item, original))
			return true;

		db.SetRetroVerdict(item.SessionId, item.OriginalId, "drop");
		await DeleteCopyAsync(thread, item);
		return false;
	}

	/// <summary>
	/// Brings a copy up to date with an edited original. Only single-message copies of
	/// plain-text originals can be re-rendered (anything with attachments, stickers,
	/// polls or forwards carries notes that can't be rebuilt without re-downloading).
	/// Returns false when the edit can't be carried across.
	/// </summary>
	private async Task<bool> TrySyncEditAsync(DiscordClient client, DiscordThreadChannel thread, RetroItem item, DiscordMessage original)
	{
		bool plain = original.Attachments is not { Count: > 0 } && original.Stickers is not { Count: > 0 }
			&& original.Poll is null && original.MessageSnapshots is not { Count: > 0 };
		if (!plain || item.ExtraCopyIds.Length > 0)
			return false;

		string text = Transposer.CopyText(importer.StrippedContent(original, item.AuthorId, item.AuthorName) ?? "", "",
			item.ReplyHeader.Length > 0 ? item.ReplyHeader : null);
		try
		{
			return await transposer.EditWebhookCopyAsync(client, thread, item.CopyId, text);
		}
		catch (Exception ex)
		{
			// e.g. the webhook was recreated and can't edit what the old one posted.
			Console.WriteLine($"[ThreadTender] Retro: couldn't sync edit to copy {item.CopyId}: {ex.Message}");
			return false;
		}
	}

	/// <summary>Bulk-deletes what Discord allows (under 14 days old), the rest one by one.</summary>
	private static async Task DeleteOriginalsAsync(DiscordChannel channel, List<DiscordMessage> originals)
	{
		DateTimeOffset bulkCutoff = DateTimeOffset.UtcNow.AddDays(-13.9); // margin under Discord's 14-day line
		List<DiscordMessage> recent = [.. originals.Where(m => m.Timestamp > bulkCutoff)];
		List<DiscordMessage> single = [.. originals.Where(m => m.Timestamp <= bulkCutoff)];

		if (recent.Count > 0)
		{
			try { await channel.DeleteMessagesAsync(recent, "ThreadTender: moved to retroactive thread"); }
			catch (Exception ex)
			{
				Console.WriteLine($"[ThreadTender] Retro bulk delete failed ({ex.Message}); deleting one by one.");
				single.AddRange(recent);
			}
		}
		foreach (DiscordMessage message in single)
		{
			try { await message.DeleteAsync("ThreadTender: moved to retroactive thread"); }
			catch (NotFoundException) { }
		}
	}

	// ================================================================ cancel / expiry / recovery

	/// <summary>
	/// Ends a session without touching the channel: deletes the staging thread if one
	/// exists. Returns false if it's too late (finalizing) or, with
	/// <paramref name="onlyIfExpired"/>, if it turned out not to be expired after all.
	/// <paramref name="force"/> skips the lock and the finalizing check — only for
	/// Finalize itself, which holds the lock, when nothing was kept.
	/// </summary>
	public async Task<bool> CancelSessionAsync(DiscordClient client, ulong sessionId, string? reason, bool force = false, bool onlyIfExpired = false)
	{
		SemaphoreSlim gate = LockFor(sessionId);
		bool locked = false;
		if (!force)
		{
			// The lock is held through Finalize; a cancel that can't get in won't barge past it.
			if (!await gate.WaitAsync(TimeSpan.FromMinutes(5)))
				return false;
			locked = true;
		}
		try
		{
			RetroSession? session = db.GetRetroSession(sessionId);
			if (session is null || !session.IsActive)
				return true;
			if (session.Status == "finalizing" && !force)
				return false;
			// Re-checked under the lock: a Stage may have extended the expiry meanwhile.
			if (onlyIfExpired && session.ExpiresAt >= DateTimeOffset.UtcNow)
				return false;

			// Mark it first so a concurrent staging loop stops at its next item.
			db.UpsertRetroSession(session with { Status = "cancelled" });
			if (session.ThreadId != 0)
			{
				try
				{
					if (await client.GetChannelAsync(session.ThreadId) is DiscordThreadChannel thread)
						await thread.DeleteAsync(reason ?? "ThreadTender: retroactive thread cancelled");
				}
				catch (NotFoundException) { }
			}
			db.CloseRetroSession(sessionId, "cancelled");
			Console.WriteLine($"[ThreadTender] Retro session {sessionId} cancelled{(reason is null ? "" : $": {reason}")}.");
			return true;
		}
		finally
		{
			if (locked)
				gate.Release();
		}
	}

	/// <summary>The per-server off switch: cancels every unfinished import in that server.</summary>
	public async Task CancelAllForGuildAsync(DiscordClient client, ulong guildId, string reason)
	{
		foreach (RetroSession session in db.GetActiveRetroSessions().Where(s => s.GuildId == guildId))
		{
			try { await CancelSessionAsync(client, session.SessionId, reason); }
			catch (Exception ex) { Console.WriteLine($"[ThreadTender] Retro cancel of {session.SessionId} failed: {ex.Message}"); }
		}
	}

	/// <summary>
	/// Called once the gateway is ready. Resumes whatever a restart interrupted, then
	/// runs a once-a-minute sweep: expired sessions are cancelled, and finalizes that
	/// failed partway are retried. Runs in the background so startup isn't held up.
	/// </summary>
	public void Start(DiscordClient client)
	{
		if (maintenanceStarted)
			return;
		maintenanceStarted = true;

		_ = Task.Run(async () =>
		{
			foreach (RetroSession session in db.GetActiveRetroSessions())
			{
				try { await RecoverAsync(client, session); }
				catch (Exception ex) { Console.WriteLine($"[ThreadTender] Retro recovery of {session.SessionId} failed: {ex}"); }
			}

			while (true)
			{
				await Task.Delay(TimeSpan.FromMinutes(1));
				foreach (RetroSession session in db.GetActiveRetroSessions())
				{
					try
					{
						if (session.Status == "finalizing")
						{
							if (finalizeBackoff.TryGetValue(session.SessionId, out var backoff) && backoff.NextTry > DateTimeOffset.UtcNow)
								continue;
							// Only if nobody is mid-finalize right now.
							SemaphoreSlim gate = LockFor(session.SessionId);
							if (!await gate.WaitAsync(0))
								continue;
							try
							{
								if (await TryFinalizeAsync(client, session.SessionId) is null)
								{
									int failures = (finalizeBackoff.TryGetValue(session.SessionId, out var prior) ? prior.Failures : 0) + 1;
									TimeSpan wait = TimeSpan.FromMinutes(Math.Min(60, 1 << Math.Min(failures - 1, 6)));
									finalizeBackoff[session.SessionId] = (failures, DateTimeOffset.UtcNow + wait);
								}
								else
								{
									finalizeBackoff.TryRemove(session.SessionId, out _);
								}
							}
							finally { gate.Release(); }
						}
						else if (session.ExpiresAt < DateTimeOffset.UtcNow)
						{
							await CancelSessionAsync(client, session.SessionId, "Review window expired", onlyIfExpired: true);
						}
					}
					catch (Exception ex) { Console.WriteLine($"[ThreadTender] Retro sweep of {session.SessionId} failed: {ex.Message}"); }
				}
			}
		});
	}

	private async Task RecoverAsync(DiscordClient client, RetroSession session)
	{
		// A finalize already under way always completes, switch or no switch: stopping
		// it would leave some originals deleted and the rest not.
		if (session.Status == "finalizing")
		{
			Console.WriteLine($"[ThreadTender] Retro: resuming finalize of session {session.SessionId}.");
			await WithLockAsync(session.SessionId, async () => await TryFinalizeAsync(client, session.SessionId));
			return;
		}

		if (!config.RetroEnabled(session.GuildId))
		{
			await CancelSessionAsync(client, session.SessionId, "Retroactive threading is switched off");
			return;
		}

		if (session.Status == "staging")
		{
			Console.WriteLine($"[ThreadTender] Retro: resuming staging of session {session.SessionId}.");
			if (await StageOrCancelAsync(client, session.SessionId) is null)
				return;
		}

		if (db.GetRetroSession(session.SessionId) is { Status: "reviewing" or "confirming" } current)
			await CatchUpVerdictsAsync(client, current);
	}

	/// <summary>
	/// Reactions sent while the bot was down never arrive as events, so reread each
	/// unsettled copy: anyone's ❌ beyond the bot's own drops it (even one already ✅'d —
	/// ❌ always wins), ✅ keeps a pending one.
	/// </summary>
	private async Task CatchUpVerdictsAsync(DiscordClient client, RetroSession session)
	{
		if (await client.GetChannelAsync(session.ThreadId) is not DiscordThreadChannel thread)
			return;

		await WithLockAsync(session.SessionId, async () =>
		{
			foreach (RetroItem item in db.GetRetroItems(session.SessionId).Where(i => i.Included && i.CopyId != 0 && i.Verdict is "pending" or "keep"))
			{
				DiscordMessage? copy = await ThreadImporter.FetchFreshAsync(thread, item.CopyId);
				if (copy is null)
				{
					db.SetRetroVerdict(session.SessionId, item.OriginalId, "drop");
					continue;
				}
				if (OthersReacted(copy, DropEmoji))
				{
					db.SetRetroVerdict(session.SessionId, item.OriginalId, "drop");
					await DeleteCopyAsync(thread, item);
				}
				else if (item.Verdict == "pending" && OthersReacted(copy, KeepEmoji))
				{
					db.SetRetroVerdict(session.SessionId, item.OriginalId, "keep");
				}
			}
			await MaybeConfirmAsync(client, session.SessionId);
		});
	}

	// ================================================================ helpers

	/// <summary>True if someone other than the bot reacted with <paramref name="emoji"/>.</summary>
	private static bool OthersReacted(DiscordMessage copy, string emoji) =>
		(copy.Reactions?.Where(r => r.Emoji.Name == emoji).Sum(r => r.Count - (r.IsMe ? 1 : 0)) ?? 0) > 0;

	/// <summary>Deletes every message of an item's copy (a long copy spans several), ignoring ones already gone.</summary>
	private static Task DeleteCopyAsync(DiscordThreadChannel thread, RetroItem item) =>
		DeleteMessagesQuietlyAsync(thread, item.AllCopyIds, "ThreadTender: copy removed from retroactive thread");

	private static async Task DeleteMessagesQuietlyAsync(DiscordThreadChannel thread, IEnumerable<ulong> ids, string reason)
	{
		foreach (ulong id in ids)
		{
			try
			{
				if (await ThreadImporter.FetchFreshAsync(thread, id) is { } message)
					await message.DeleteAsync(reason);
			}
			catch (NotFoundException) { }
		}
	}

	/// <summary>True only if every message of the item's copy still exists.</summary>
	private static async Task<bool> CopyIntactAsync(DiscordThreadChannel thread, RetroItem item)
	{
		foreach (ulong id in item.AllCopyIds)
			if (await ThreadImporter.FetchFreshAsync(thread, id) is null)
				return false;
		return true;
	}

	private static async Task DeleteThreadQuietlyAsync(DiscordThreadChannel thread)
	{
		try { await thread.DeleteAsync("ThreadTender: retroactive thread cancelled"); }
		catch (NotFoundException) { }
	}

	private async Task WithLockAsync(ulong sessionId, Func<Task> action)
	{
		SemaphoreSlim gate = LockFor(sessionId);
		await gate.WaitAsync();
		try { await action(); }
		finally { gate.Release(); }
	}

	private static string Label(DiscordMessage m)
	{
		string author = (m.Author as DiscordMember)?.DisplayName ?? m.Author?.GlobalName ?? m.Author?.Username ?? "someone";
		string[] words = (m.Content ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		string text = words.Length switch
		{
			0 => m.Attachments is { Count: > 0 } ? "(attachment)" : "(no text)",
			<= 8 => string.Join(' ', words),
			_ => string.Join(' ', words.Take(8)) + "…",
		};
		string label = $"{author}: {text}";
		return label.Length <= 100 ? label : label[..99] + "…";
	}

	private static string Snippet(string? text, int max)
	{
		string clean = (text ?? "").Replace('\n', ' ').Trim();
		return clean.Length <= max ? clean : clean[..max];
	}

	private static Task RespondEphemeralAsync(DiscordInteraction interaction, string content) =>
		interaction.CreateResponseAsync(DiscordInteractionResponseType.ChannelMessageWithSource,
			new DiscordInteractionResponseBuilder().WithContent(content).AsEphemeral());

	private static Task EditOriginalAsync(DiscordInteraction interaction, string content) =>
		interaction.EditOriginalResponseAsync(new DiscordWebhookBuilder().WithContent(content));
}
