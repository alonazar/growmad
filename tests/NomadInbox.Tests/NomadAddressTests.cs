using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NomadInbox.Core.Domain;
using NomadInbox.Infrastructure.Ai;

namespace NomadInbox.Tests;

/// <summary>NOMAD's services are addressed by container name (works on NOMAD's network and via compose's extra_hosts).</summary>
public class NomadAddressTests
{
    [Fact]
    public void Defaults_use_nomad_container_names()
    {
        var settings = new InboxSettings();

        Assert.Equal("http://nomad_ollama:11434", settings.OllamaUrl);
        Assert.Equal("http://nomad_admin:8080", settings.NomadUrl);
    }

    [Fact]
    public async Task Container_names_with_underscores_are_valid_addresses()
    {
        var handler = new RecordingHandler("""{"version":"0.33.3"}""");
        var ollama = new OllamaClient(new HttpClient(handler));

        Assert.Equal("0.33.3", await ollama.VersionAsync("http://nomad_ollama:11434", CancellationToken.None));
        Assert.Equal("http://nomad_ollama:11434/api/version", handler.LastUri?.ToString());
    }

    [Theory]
    [InlineData("http://host.docker.internal:11434", "http://host.docker.internal:8080", "http://nomad_ollama:11434", "http://nomad_admin:8080")]
    [InlineData("http://192.168.1.20:11434", "http://nomad.lan:8080", "http://192.168.1.20:11434", "http://nomad.lan:8080")]
    public async Task Upgrading_replaces_only_the_old_default_addresses(string ollama, string nomad, string expectedOllama, string expectedNomad)
    {
        await using var test = await TestDatabase.CreateAsync();
        await test.Settings.SaveAsync(new InboxSettings(), newPassword: null);

        await using (var db = await test.Factory.CreateDbContextAsync())
        {
            // Back to the schema before the migration (later migrations drop their columns), with the old addresses.
            var migrator = db.GetService<IMigrator>();
            var migration = db.Database.GetMigrations().Single(m => m.EndsWith("_NomadNetworkAddresses"));
            await migrator.MigrateAsync(db.Database.GetMigrations().TakeWhile(m => m != migration).Last());
            await db.Database.ExecuteSqlAsync($"UPDATE Settings SET OllamaUrl = {ollama}, NomadUrl = {nomad}");
            await migrator.MigrateAsync();
        }

        var settings = await test.Settings.GetAsync();
        Assert.Equal(expectedOllama, settings.OllamaUrl);
        Assert.Equal(expectedNomad, settings.NomadUrl);
    }

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
