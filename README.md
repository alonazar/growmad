# NOMAD Inbox

Email-driven intake for a [Project NOMAD](https://github.com/Crosstalk-Solutions/project-nomad) server.
Send mail to a Gmail account with a subject like

```
NOMAD: Survival/Water #filtration #diy priority=high Ceramic filter builds
```

and NOMAD Inbox records it, indexes it by the subject line, files its attachments, collects its links, and (in later phases) summarizes those links.

**Status: Phase 5.** Done so far:
- **Phase 1:** IMAP polling, subject indexing, sender allowlist, a web UI with login, SQLite storage and the Docker setup.
- **Phase 2:** full message processing. That covers the body text, link extraction and classification, attachment filing with de-duplication and text extraction, and Gmail labels.
- **Phase 3:** web links are fetched, their readable text is extracted, and NOMAD's Ollama summarizes them.
- **Phase 4:** YouTube videos are summarized from their transcripts, channels and playlists get a profile, and followed channels add their new videos automatically.
- **Phase 5:** everything goes into the knowledge base of NOMAD's AI Assistant, so you can ask about it in NOMAD's chat; items can be deleted.

Hardening comes next (see [Roadmap](#roadmap)).

## Stack

- .NET 10 (LTS), ASP.NET Core, Blazor (interactive server rendering)
- MailKit for IMAP. It never marks mail as read; the only change it makes to the mailbox is adding labels.
- EF Core + SQLite; the IMAP app password is encrypted with ASP.NET Data Protection
- PdfPig and Open XML SDK for text extraction from attachments
- SmartReader (a port of Firefox's Reader View) for article text, Markdig for rendering summaries, Ollama's HTTP API for the summaries
- Docker Compose, running next to NOMAD on the same Ubuntu/WSL host

```
src/NomadInbox.Core            domain model, subject parser, link extractor, HTML-to-text, mailbox interfaces
src/NomadInbox.Infrastructure  EF Core, MailKit reader/session, ingest + processor, attachment store, text extraction
src/NomadInbox.Web             Blazor UI, login, downloads, startup
tests/NomadInbox.Tests         xUnit tests
```

## Subject syntax

| Part | Example | Meaning |
|---|---|---|
| prefix | `NOMAD:` | Required. Configurable. `Re:`/`Fwd:` in front are ignored. |
| category | `Survival/Water` | First token, if it contains `/`. Single level: `Survival/` or `cat=Survival`. Quote names with spaces: `"Medical/First Aid"`. |
| tags | `#filtration` | Any number; stored lower-case. |
| fields | `priority=high`, `source="Some Channel"` | Free key/value pairs. |
| title | `Ceramic filter builds` | Everything else. |

The Settings page has a live preview.

## Gmail setup

### 1. Create a dedicated Gmail account (recommended)

Create a new account at <https://accounts.google.com/signup>, e.g. `yourname.nomad@gmail.com`. Keeping it separate means NOMAD Inbox never has access to your personal mail.

### 2. Turn on 2-Step Verification

Signed in as the new account:

1. Go to <https://myaccount.google.com/security>.
2. Under **How you sign in to Google**, turn on **2-Step Verification**. A phone number or the Google Authenticator app is fine.

Google only offers app passwords once 2-Step Verification is on.

### 3. Create an app password

1. Go to <https://myaccount.google.com/apppasswords>.
2. Name it `NOMAD Inbox` and click **Create**.
3. Google shows a 16-character code like `abcd efgh ijkl mnop`. **Copy it now**, because Google won't show it again. The spaces don't matter; NOMAD Inbox strips them.

If that page says *"The setting you're looking for is not available"*, either 2-Step Verification isn't fully on yet, or the account uses only security keys or Advanced Protection.

### 4. Enter it in NOMAD Inbox

Open `http://localhost:8150/settings` and sign in as `admin`. To see the admin password, run this in Ubuntu:

```bash
grep PASSWORD ~/nomad-inbox/.env
```

Then fill in:

| Field | Value |
|---|---|
| IMAP server / Port | leave `imap.gmail.com` / `993` |
| Email address | the new Gmail address |
| App password | the 16-character code |
| Folder | leave `INBOX` |
| Subject prefix | leave `NOMAD:` (or change it) |
| **Allowed senders** | the address(es) **you'll send from**, one per line, e.g. your personal Gmail. You can also allow a whole domain with `@yourdomain.com`. Mail from anyone else is recorded as *Ignored* and never processed. |
| On first scan, skip mail before | today's date (optional, skips Google's welcome emails) |

Then:

1. Click **Test connection**. You should see *Connected. "INBOX" contains N messages.*
   - *Login failed* means the app password is wrong or 2-Step Verification is off.
2. Turn on **Check for new mail automatically**. Every 5 minutes is fine.
3. Click **Save settings**. Saving starts a check right away.

### 5. Send a test email

From an allowed address, send an email to the new Gmail account with a subject like:

```
NOMAD: Survival/Water #filtration #test Ceramic filter test
```

Put a link or two in the body and attach a file. Then click **Check mail now** on the dashboard, or wait for the next check. The email should appear with category `Survival/Water` and tags `filtration` and `test`. Once it's processed, its links and attachments are listed on its page, and Gmail shows it with the `NOMAD/Processed` label.

If it shows as **Ignored**, the sender address isn't on your allowlist exactly as Gmail reports it.

## Run on the NOMAD host (Ubuntu / WSL2)

Requires Docker, which the NOMAD installer already set up.

```bash
git clone https://github.com/alonazar/growmad.git ~/nomad-inbox && cd ~/nomad-inbox
cp .env.example .env    # set NOMADINBOX_ADMIN_PASSWORD (and TZ)
docker compose up -d --build
```

The repository is private. On WSL, let Ubuntu's git use the Windows GitHub login, through Git for Windows' credential manager:

```bash
git config --global credential.helper '/mnt/c/Program\ Files/Git/ucrt64/bin/git-credential-manager.exe'
```

Open `http://<host>:8150` (from Windows with WSL2: `http://localhost:8150`). Port 8150 avoids NOMAD's own apps (8080, 8090, 8100-8500, 9999, 11434).

Data lives in the `nomad-inbox-data` volume:
- the database
- `keys/`, which decrypts the stored app password (back it up together with the database)
- `files/`, the filed attachments

To update to the latest version on GitHub (your `.env` and data are kept; database migrations run automatically on start):

```bash
cd ~/nomad-inbox && git pull && docker compose up -d --build
```

**WSL note:** WSL shuts down idle distros, which stops polling. Keep the distro running, for example by starting it at Windows login, or by setting `vmIdleTimeout=-1` under `[wsl2]` in `%UserProfile%\.wslconfig`.

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

## How it works

Each check has two steps, and both share one schedule ("Check mail now" runs both straight away).

**1. Poll.** NOMAD Inbox connects over IMAPS, opens the folder read-only and fetches the headers of messages newer than the last run. It tracks that position by UID, per folder, together with UIDVALIDITY, and checks the subject prefix itself. It doesn't use IMAP SEARCH for new mail, because Gmail's search index can lag behind delivery, and a message missed that way would be skipped for good. Only the first scan of a folder uses SEARCH, plus the newest 50 messages fetched directly. New matches are de-duplicated by Message-ID and stored as *Pending*. Senders not on the allowlist are stored as *Ignored*. "Rescan the folder from the start" in Settings resets the scan position.

**2. Process.** Pending emails are handled in batches of 25 over a single connection:

- **Fetch.** Gets the full message by UID, or by Message-ID if the folder's UIDs were reset. It uses `BODY.PEEK`, so mail stays unread.
- **Body.** Keeps the plain-text body. If there's only HTML, it's converted to text.
- **Links.** Extracts every http(s) link and normalizes it:
  - Tracking parameters (`utm_*`, `fbclid`, `si`, …) are removed.
  - Google redirect links are unwrapped.
  - All YouTube forms (`youtu.be`, `shorts`, `m.youtube.com`, …) are rewritten to one canonical URL.

  Each link is then labelled as *Web*, *YouTube video*, *YouTube playlist* or *YouTube channel*.
- **Attachments.** Files are stored under `/data/files/{Category}/{yyyy-MM}/{name}`:
  - A file identical (by SHA-256) to one already filed isn't stored twice.
  - Files over the size limit in Settings (50 MB by default) are listed but not stored.
  - Text is extracted from PDF, DOCX, PPTX, HTML, EML and plain-text files for search and later summaries.
  - Download files from the email's page.
- **Label.** Adds `NOMAD/Processed` or `NOMAD/Failed` in Gmail. Both labels can be changed or turned off in Settings.

A failed email shows the reason on its page and has a **Reprocess** button. Ignored emails have **Process anyway**.

**3. Links.** A separate background worker, so slow summaries never hold up mail checks, handles each *Web* link in two steps:

- **Fetch.** Downloads the page (up to 10 MB, 45 s timeout). Addresses on your own network (`localhost`, `192.168.x.x`, NOMAD's own services, …) are refused unless **Allow links to local-network addresses** is on in Settings. The check happens when the connection is made, so redirects can't get around it.
- **Readable text.** For HTML, the article body is extracted the way a browser's Reader View does it, falling back to all visible text for pages that aren't articles. PDFs and plain-text links are read directly. Images, archives and other binary files are *Skipped*.
- **JavaScript pages.** If an HTML page yields under 500 characters (typical of single-page and WebAssembly apps that only send a loading shell), it's opened in headless Chromium through Playwright:
  - It waits for the network to go quiet and the visible text to stop changing (up to about a minute), then extracts the text again. The version with more text wins.
  - The local-network rule applies to every request the page makes, and images, fonts and media aren't loaded.
  - It can be turned off in Settings. The browser is installed into the Docker image; for local development, run `dotnet src/NomadInbox.Web/bin/Debug/net10.0/NomadInbox.Web.dll install-browser` once.
- **Summary.** The text is sent to NOMAD's Ollama with the email's category, tags and title as context. The answer is a bold one-line overview followed by key-point bullets. Pages too long for the context window are condensed part by part, then summarized together.

If Ollama isn't installed, isn't running, or no model is chosen yet, links wait as *Waiting for AI summary* and are summarized once it's available (checked every 2 minutes and whenever Settings are saved). A summary that errors is retried twice, then marked *Failed*. Each link has **Retry** or **Summarize again** on the email's page.

**4. YouTube.** YouTube links go through the same fetch → summarize steps, using [yt-dlp](https://github.com/yt-dlp/yt-dlp). It's installed in the image and updates itself daily, and uses Playwright's bundled Node.js to solve YouTube's JavaScript challenges.

- **Videos.** Title, channel, length, publish date, description and chapters, plus a transcript from the captions. The caption track is chosen in this order:
  1. uploaded subtitles in a preferred language (Settings → YouTube, default `en`)
  2. the video's original-language auto-captions (`xx-orig`)
  3. any uploaded subtitles
  4. translated auto-captions (YouTube rate-limits these with HTTP 429)

  The transcript is cleaned of YouTube's rolling-caption repeats and gets a `[m:ss]` marker about every 90 seconds. The summary ends with a chapter list when the video has chapters. Videos without captions are summarized from their description.
- **Channels and playlists.** Description, subscriber count and the 15 most recent videos, summarized as what the channel covers, who it's for, and which recent videos look most useful.
- **Following channels.** Use **Follow channel** on any YouTube channel or video link, or tag the email `#follow`, to follow that channel:
  - Every few hours (6 by default) NOMAD Inbox reads the channel's public RSS feed.
  - Each video published since you followed becomes an item in the inbox, filed under the original email's category and tags, then summarized like any other video. At most 5 new videos are added per channel per check.
  - Manage follows on the **Channels** page: check now, pause, unfollow.

- **Saving videos.** With **Settings → YouTube → Download videos**, the video itself is saved for offline viewing. The choices are *Never*, *Only for emails tagged `#download`* (the default) and *Always*, with a quality cap (720p by default) and a size limit per video (2 GB by default).
  - yt-dlp merges video and audio into one MP4 with ffmpeg.
  - Files go to NOMAD's shared media folder as `/opt/project-nomad/storage/media/youtube/<Channel>/<yyyy-MM-dd> <Title> [<id>].mp4`, next to a `-thumb.jpg` and a `.nfo` file. The `.nfo` holds title, date, channel, category, tags, and the AI summary as the plot.
  - In **Jellyfin**, add a library of type *Movies* pointing at `/media/youtube` and turn off its internet metadata downloaders; it then shows each video with its summary, offline. **File Browser** sees the same files under `media/youtube`.
  - Videos also play on the email's page. A channel followed from a `#download` email downloads its new videos too. A one-off `nomad-inbox-init` service in `compose.yaml` creates the folder and makes it writable for the app; set `NOMADINBOX_VIDEO_DIR` in `.env` to use another folder.

**5. NOMAD knowledge base.** A background sync puts everything into the knowledge base of NOMAD's AI Assistant, the documents its chat answers from, using the Command Center's API (`/api/rag/...`, no login needed on the local network). What goes in:

- **Each summarized link** (web page, video, channel) as a Markdown document: title, source, site or channel, date, length, category and tags, the AI summary, chapters, and the full article or transcript. The full text is what makes answers specific; it can be turned off.
- **Each stored attachment** NOMAD can read itself (PDF, Word, EPUB, text, images) as the original file. Other files with extracted text (PowerPoint, forwarded emails) go in as Markdown.
- **An email note** when the email body has text of its own beyond links.

How it's filed and kept in step:

- **Collections.** Documents go into a collection named after the item's top-level category (`Survival/Water` → `survival`), so whole areas can be switched on or off in NOMAD's chat. Uncategorized items go into `nomad inbox`.
- **Reconciliation.** The sync compares what *should* be in NOMAD with what NOMAD Inbox has sent: it uploads new documents, replaces changed ones (e.g. after **Summarize again**), and removes documents whose item was deleted. It runs after changes and every 2 minutes, sends at most 10 uploads per run, and shows NOMAD's embedding state (*Embedding…* → *Indexed · N chunks*) on each item's page.
- **Controls.** **Resend** (per item) and **Resend everything** (Settings) send again. If NOMAD isn't reachable, it simply waits.
- **Deleting.** **Delete…** on an item's page removes it with its links and attachments, and optionally its files: attachments and saved videos, unless another item uses them. Its documents are removed from NOMAD on the next sync, and its Message-ID is remembered, so a folder rescan or channel check won't bring it back.

### Setting up summaries

1. In NOMAD's Command Center, install the **AI Assistant** (it runs Ollama in the `nomad_ollama` container on port 11434).
2. In the AI Assistant's settings, download a model. On a 16 GB GPU, `gemma3:12b` is a good balance of quality and room for long pages. Smaller GPUs can use `gemma3:4b` or `qwen3:8b`.
3. In NOMAD Inbox → **Settings → AI summaries**:
   - leave the address as `http://host.docker.internal:11434`
   - click **Load models** and choose the model
   - click **Test the model**, then **Save settings**

The summary instructions can be changed under **Summary instructions (advanced)**.

## Roadmap

1. ~~Skeleton, IMAP ingest, UI, Docker~~
2. ~~Fetch full messages: file attachments (with de-duplication and text extraction), extract links, label processed mail in Gmail~~
3. ~~Web links: readable-text extraction and summaries from NOMAD's Ollama~~
4. ~~YouTube: video transcripts with yt-dlp, channel profiles, following channels~~
5. ~~Push summaries and documents into NOMAD's knowledge base~~ (optional Kiwix ZIM archives: not done)
6. Hardening: retries with backoff, backups, OCR for scanned PDFs
