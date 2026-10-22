using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Settings;
using NomadInbox.Infrastructure.YouTube;

namespace NomadInbox.Infrastructure.Ingest;

/// <summary>
/// Followed YouTube channels: new uploads (from the channel's RSS feed) become items in the inbox, filed under the
/// category and tags of the email that started the follow, and their video links go through the normal pipeline.
/// </summary>
public sealed class FollowService(
    IDbContextFactory<NomadInboxDbContext> dbFactory,
    IYouTubeClient youTube,
    LinkTrigger linkTrigger,
    ILogger<FollowService> logger)
{
    /// <summary>A channel that uploads a lot (or a long outage) won't flood the inbox: only the newest few per check.</summary>
    public const int MaxNewPerCheck = 5;
    public const string FollowTag = "follow";

    /// <summary>Follows a channel (or re-enables it). Only videos published from now on are picked up.</summary>
    public async Task<FollowedChannel> FollowAsync(string channelId, string name, string url, string? category,
        IEnumerable<string> tags, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channel = await db.FollowedChannels.SingleOrDefaultAsync(c => c.ChannelId == channelId, ct);
        if (channel is null)
        {
            channel = new FollowedChannel
            {
                ChannelId = channelId,
                Name = name,
                Url = url,
                Category = category,
                Tags = tags.Where(t => !t.Equals(FollowTag, StringComparison.OrdinalIgnoreCase)).Distinct().ToList(),
            };
            db.FollowedChannels.Add(channel);
            logger.LogInformation("Following {Channel} ({ChannelId})", name, channelId);
        }
        else if (!channel.Enabled)
        {
            channel.Enabled = true;
            channel.FollowedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        return channel;
    }

    /// <summary>Checks every enabled channel whose last check is older than <paramref name="intervalHours"/>.</summary>
    public async Task<int> CheckDueAsync(int intervalHours, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddHours(-Math.Max(1, intervalHours));
        var due = (await db.FollowedChannels.Where(c => c.Enabled).ToListAsync(ct))
            .Where(c => c.LastCheckedAt is null || c.LastCheckedAt < cutoff)
            .ToList();

        var total = 0;
        foreach (var channel in due) total += await CheckChannelAsync(db, channel, ct);
        return total;
    }

    /// <summary>"Check now" from the Channels page.</summary>
    public async Task<int> CheckAsync(int followedChannelId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var channel = await db.FollowedChannels.SingleOrDefaultAsync(c => c.Id == followedChannelId, ct);
        return channel is null ? 0 : await CheckChannelAsync(db, channel, ct);
    }

    private async Task<int> CheckChannelAsync(NomadInboxDbContext db, FollowedChannel channel, CancellationToken ct)
    {
        try
        {
            var entries = await youTube.GetFeedAsync(channel.ChannelId, ct);
            var candidates = entries.Where(e => e.PublishedAt > channel.FollowedAt).OrderBy(e => e.PublishedAt).ToList();
            var ids = candidates.Select(e => MessageIdFor(e.VideoId)).ToList();
            var known = (await db.Emails.Where(e => ids.Contains(e.MessageId)).Select(e => e.MessageId).ToListAsync(ct)).ToHashSet();
            known.UnionWith(await db.DeletedMessages.Where(d => ids.Contains(d.MessageId)).Select(d => d.MessageId).ToListAsync(ct));
            var fresh = candidates.Where(e => !known.Contains(MessageIdFor(e.VideoId))).TakeLast(MaxNewPerCheck).ToList();

            foreach (var entry in fresh)
            {
                db.Emails.Add(new InboxEmail
                {
                    MessageId = MessageIdFor(entry.VideoId),
                    Folder = "YouTube",
                    FromAddress = "youtube",
                    FromName = channel.Name,
                    Subject = $"New video from {channel.Name}: {entry.Title}",
                    ReceivedAt = entry.PublishedAt,
                    Category = channel.Category,
                    Tags = channel.Tags.ToList(),
                    Title = entry.Title,
                    Status = EmailStatus.Done,
                    ProcessedAt = DateTimeOffset.UtcNow,
                    Source = ItemSource.FollowedChannel,
                    FollowedChannelId = channel.Id,
                    LinkCount = 1,
                    Links = [new EmailLink { Url = $"https://www.youtube.com/watch?v={entry.VideoId}", Kind = LinkKind.YouTubeVideo }],
                });
            }

            channel.LastCheckedAt = DateTimeOffset.UtcNow;
            channel.LastError = null;
            channel.VideosFound += fresh.Count;
            await db.SaveChangesAsync(ct);

            if (fresh.Count > 0)
            {
                logger.LogInformation("{Count} new video(s) from followed channel {Channel}", fresh.Count, channel.Name);
                linkTrigger.Signal();
            }
            return fresh.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning("Checking followed channel {Channel} failed: {Message}", channel.Name, ex.Message);
            channel.LastCheckedAt = DateTimeOffset.UtcNow;
            channel.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
            return 0;
        }
    }

    public static string MessageIdFor(string videoId) => $"youtube:{videoId}";
}

/// <summary>Checks followed channels on the configured interval (looks every 10 minutes for channels that are due).</summary>
public sealed class FollowWorker(FollowService follows, SettingsService settingsService, ILogger<FollowWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // let startup settle
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var settings = await settingsService.GetAsync(stoppingToken);
                await follows.CheckDueAsync(settings.FollowCheckHours, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Checking followed channels failed");
            }
            await Task.Delay(Tick, stoppingToken);
        }
    }
}
