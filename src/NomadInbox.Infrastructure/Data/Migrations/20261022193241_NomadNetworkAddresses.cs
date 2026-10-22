using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NomadInbox.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Hand-written: NOMAD's Ollama and Command Center are now addressed by container name, which works both when
    /// NOMAD runs this app (Supply Depot, on NOMAD's network) and with compose.yaml (extra_hosts). Only the old
    /// defaults are replaced; addresses someone typed in stay as they are.
    /// </summary>
    public partial class NomadNetworkAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Settings SET OllamaUrl = 'http://nomad_ollama:11434' WHERE OllamaUrl = 'http://host.docker.internal:11434';");
            migrationBuilder.Sql("UPDATE Settings SET NomadUrl = 'http://nomad_admin:8080' WHERE NomadUrl = 'http://host.docker.internal:8080';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE Settings SET OllamaUrl = 'http://host.docker.internal:11434' WHERE OllamaUrl = 'http://nomad_ollama:11434';");
            migrationBuilder.Sql("UPDATE Settings SET NomadUrl = 'http://host.docker.internal:8080' WHERE NomadUrl = 'http://nomad_admin:8080';");
        }
    }
}
