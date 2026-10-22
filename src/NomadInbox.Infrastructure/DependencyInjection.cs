using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NomadInbox.Core.Mail;
using NomadInbox.Infrastructure.Ai;
using NomadInbox.Infrastructure.Web;
using NomadInbox.Infrastructure.YouTube;
using NomadInbox.Infrastructure.Data;
using NomadInbox.Infrastructure.Files;
using NomadInbox.Infrastructure.Ingest;
using NomadInbox.Infrastructure.Mail;
using NomadInbox.Infrastructure.Settings;

namespace NomadInbox.Infrastructure;

public static class DependencyInjection
{
    /// <param name="dataDirectory">Holds the SQLite database and Data Protection keys (a Docker volume in production).</param>
    /// <param name="videoDirectory">Where downloaded videos go (NOMAD's shared media folder in production); defaults to data/videos.</param>
    public static IServiceCollection AddNomadInbox(this IServiceCollection services, string dataDirectory, string? videoDirectory = null)
    {
        Directory.CreateDirectory(dataDirectory);

        services.AddDbContextFactory<NomadInboxDbContext>(o =>
            o.UseSqlite($"Data Source={Path.Combine(dataDirectory, "nomad-inbox.db")}"));

        // Encrypts the stored IMAP password. Losing the keys folder means re-entering the password.
        services.AddDataProtection()
            .SetApplicationName("NomadInbox")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

        services.AddSingleton<SettingsService>();
        services.AddSingleton<IMailboxReader, MailKitMailboxReader>();
        services.AddSingleton<MailIngestService>();
        services.AddSingleton(new AttachmentStore(Path.Combine(dataDirectory, "files")));
        services.AddSingleton<TextExtractor>();
        services.AddSingleton<EmailProcessor>();

        // Phase 3: web links → readable text → summary from NOMAD's Ollama.
        services.AddSingleton<PageFetcher>();
        services.AddSingleton<ReadableText>();
        services.AddSingleton<IPageRenderer, PlaywrightRenderer>();
        services.AddSingleton(new OllamaClient(new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }) { Timeout = Timeout.InfiniteTimeSpan })); // each call sets its own timeout
        services.AddSingleton<IOllamaApi>(sp => sp.GetRequiredService<OllamaClient>());
        services.AddSingleton<Summarizer>();
        // Phase 4: YouTube videos, channels, playlists and followed channels.
        services.AddSingleton<YtDlp>();
        services.AddSingleton<IYouTubeClient>(sp => new YtDlpYouTubeClient(
            sp.GetRequiredService<YtDlp>(),
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<YtDlpYouTubeClient>>()));
        services.AddSingleton<FollowService>();
        services.AddHostedService<FollowWorker>();
        services.AddHostedService<YtDlpMaintenance>();
        services.AddSingleton(sp => new VideoDownloader(
            sp.GetRequiredService<YtDlp>(),
            videoDirectory ?? Path.Combine(dataDirectory, "videos"),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<VideoDownloader>>()));
        services.AddSingleton<VideoTrigger>();
        services.AddHostedService<VideoDownloadWorker>();

        services.AddSingleton<LinkProcessor>();
        services.AddSingleton<LinkTrigger>();
        services.AddHostedService<LinkWorker>();
        services.AddSingleton<PollerStatus>();
        services.AddSingleton<PollTrigger>();
        services.AddHostedService<MailPollerWorker>();

        return services;
    }
}
