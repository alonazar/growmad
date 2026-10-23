# NOMAD Inbox: development

The stack, the project's status, and how to build, test and release it. For what the app does, see the [README](README.md); to install it, see [README-SETUP](README-SETUP.md).

- [Status](#status)
- [Stack](#stack)
- [Develop](#develop)
- [Releasing a version](#releasing-a-version)
- [Roadmap](#roadmap)

## Status

**Phase 5.** Done so far:
- **Phase 1:** IMAP polling, subject indexing, sender allowlist, a web UI with login, SQLite storage and the Docker setup.
- **Phase 2:** full message processing. That covers the body text, link extraction and classification, attachment filing with de-duplication and text extraction, and Gmail labels.
- **Phase 3:** web links are fetched, their readable text is extracted, and NOMAD's Ollama summarizes them.
- **Phase 4:** YouTube videos are summarized from their transcripts, channels and playlists get a profile, and followed channels add their new videos automatically.
- **Phase 5:** everything goes into the knowledge base of NOMAD's AI Assistant, so you can ask about it in NOMAD's chat; items can be deleted.
- **Saved pages:** local copies of web pages (content with its formatting, images, videos and links), read in the **Library** through an editable template, and copied as plain HTML into NOMAD's File Browser.
- **Since then:** style skins (Settings → Appearance), installing from NOMAD's Supply Depot, and the attachment filter (Settings → Attachments).

Hardening comes next (see [Roadmap](#roadmap)).

## Stack

- .NET 10 (LTS), ASP.NET Core, Blazor (interactive server rendering)
- MailKit for IMAP. It never marks mail as read; the only change it makes to the mailbox is adding labels.
- EF Core + SQLite; the IMAP app password is encrypted with ASP.NET Data Protection
- PdfPig and Open XML SDK for text extraction from attachments
- SmartReader (a port of Firefox's Reader View) for article text, Markdig for rendering summaries, Ollama's HTTP API for the summaries
- Docker: a NOMAD Supply Depot custom app (image on GHCR), or Docker Compose next to NOMAD on the same Ubuntu/WSL host

```
src/NomadInbox.Core            domain model, subject parser, link extractor, HTML-to-text, mailbox interfaces
src/NomadInbox.Infrastructure  EF Core, MailKit reader/session, ingest + processor, attachment store, text extraction
src/NomadInbox.Web             Blazor UI, login, downloads, startup
tests/NomadInbox.Tests         xUnit tests
```

## Develop

```bash
dotnet tool restore
dotnet run --project src/NomadInbox.Web     # login admin / admin (appsettings.Development.json)
dotnet test
```

Local data goes to `src/NomadInbox.Web/data/`. To add a migration:

```bash
dotnet ef migrations add <Name> -p src/NomadInbox.Infrastructure -s src/NomadInbox.Web -o Data/Migrations
```

Migrations are applied automatically at startup.

The headless browser for JavaScript pages is installed into the Docker image. For local development, and before running the page-rendering tests, install it once:

```bash
dotnet run --project src/NomadInbox.Web -- install-browser
```

## Releasing a version

Pull requests build the Docker image and smoke-test it ([workflow](.github/workflows/image.yml)). Tagging `main` publishes it for the Supply Depot:

```bash
git checkout main && git pull
git tag v1.0.6 && git push origin v1.0.6
```

That publishes `ghcr.io/alonazar/nomad-inbox:1.0.6` and moves `:latest` to it, so **Update (pull latest)** in NOMAD installs it (see [Updating](README.md#updating)). Use the next unused `vX.Y.Z`: NOMAD and the workflow only recognize tags of that form. Tag the merged `main`, not an older checkout.

The package is public and the repository's workflow can write to it. Should it ever be recreated: make it public (**Packages › nomad-inbox › Package settings › Change visibility**) and give the repository write access there (**Manage Actions access**), or the publish step fails with `permission_denied: write_package`.

## Roadmap

1. ~~Skeleton, IMAP ingest, UI, Docker~~
2. ~~Fetch full messages: file attachments (with de-duplication and text extraction), extract links, label processed mail in Gmail~~
3. ~~Web links: readable-text extraction and summaries from NOMAD's Ollama~~
4. ~~YouTube: video transcripts with yt-dlp, channel profiles, following channels~~
5. ~~Push summaries and documents into NOMAD's knowledge base~~ (optional Kiwix ZIM archives: not done)
6. Hardening: retries with backoff, backups, OCR for scanned PDFs
