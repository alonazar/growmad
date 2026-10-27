# NOMAD Inbox

Email-driven intake for a [Project NOMAD](https://github.com/Crosstalk-Solutions/project-nomad) server.
Send mail to a Gmail account with a subject like

```
NOMAD: Survival/Water #filtration #diy priority=high Ceramic filter builds
```

and NOMAD Inbox records it, indexes it by the subject line, files its attachments, collects its links and summarizes them with NOMAD's AI. Web pages can be saved for offline reading, YouTube videos get transcripts, summaries and optional downloads, and everything goes into the knowledge base of NOMAD's AI Assistant, so you can ask about it in NOMAD's chat.

- **Install it:** [README-SETUP](README-SETUP.md): NOMAD's Supply Depot, Gmail, AI summaries
- **Work on it:** [README-DEV](README-DEV.md): status, stack, development, releases, roadmap

## Contents

- [Subject syntax](#subject-syntax)
- [How it works](#how-it-works)
  1. [Poll](#1-poll) · 2. [Process](#2-process) · 3. [Links](#3-links) · 4. [YouTube](#4-youtube) · 5. [NOMAD knowledge base](#5-nomad-knowledge-base) · 6. [Saved pages](#6-saved-pages) · 7. [Image descriptions](#7-image-descriptions) · 8. [Air-gapped operation](#8-air-gapped-operation)
- [Updating](#updating)
- [Setup](README-SETUP.md): [Supply Depot install](README-SETUP.md#install-from-nomads-supply-depot-recommended) · [Gmail](README-SETUP.md#gmail-setup) · [AI summaries](README-SETUP.md#ai-summaries) · [Moving an install.sh install](README-SETUP.md#move-an-install-made-with-installsh) · [install.sh](README-SETUP.md#run-with-installsh-instead-ubuntu--wsl2)
- [Development](README-DEV.md): [Status](README-DEV.md#status) · [Stack](README-DEV.md#stack) · [Develop](README-DEV.md#develop) · [Releasing a version](README-DEV.md#releasing-a-version) · [Roadmap](README-DEV.md#roadmap)

## Subject syntax

| Part | Example | Meaning |
|---|---|---|
| prefix | `NOMAD:` | Required. Configurable. `Re:`/`Fwd:` in front are ignored. |
| category | `Survival/Water` | First token, if it contains `/`. Single level: `Survival/` or `cat=Survival`. Quote names with spaces: `"Medical/First Aid"`. |
| tags | `#filtration` | Any number; stored lower-case. |
| fields | `priority=high`, `source="Some Channel"` | Free key/value pairs. |
| title | `Ceramic filter builds` | Everything else. |

The Settings page has a live preview.

## How it works

Each check has two steps, and both share one schedule ("Check mail now" runs both straight away).

### 1. Poll

NOMAD Inbox connects over IMAPS, opens the folder read-only and fetches the headers of messages newer than the last run. It tracks that position by UID, per folder, together with UIDVALIDITY, and checks the subject prefix itself. It doesn't use IMAP SEARCH for new mail, because Gmail's search index can lag behind delivery, and a message missed that way would be skipped for good. Only the first scan of a folder uses SEARCH, plus the newest 50 messages fetched directly. New matches are de-duplicated by Message-ID and stored as *Pending*. Senders not on the allowlist are stored as *Ignored*. "Rescan the folder from the start" in Settings resets the scan position.

### 2. Process

Pending emails are handled in batches of 25 over a single connection:

- **Fetch.** Gets the full message by UID, or by Message-ID if the folder's UIDs were reset. It uses `BODY.PEEK`, so mail stays unread.
- **Body.** Keeps the plain-text body. If there's only HTML, it's converted to text.
- **Links.** Extracts every http(s) link and normalizes it:
  - Tracking parameters (`utm_*`, `fbclid`, `si`, …) are removed.
  - Google redirect links are unwrapped.
  - All YouTube forms (`youtu.be`, `shorts`, `m.youtube.com`, …) are rewritten to one canonical URL.

  Each link is then labelled as *Web*, *YouTube video*, *YouTube playlist* or *YouTube channel*.
- **Attachments.** Files are stored under `/data/files/{Category}/{yyyy-MM}/{name}`:
  - A file identical (by SHA-256) to one already filed isn't stored twice.
  - Files over the size limit in **Settings → Attachments** (50 MB by default) are listed but not stored.
  - The same section has a switch per kind: images, videos, audio, documents and text, archives, other files, and programs and scripts. Files of a kind that's off are listed but not stored.
  - Programs and scripts are off by default. They're recognized by name, by content type and by their bytes (Windows, Linux and macOS programs, `#!` scripts, zips with a program inside, also one zip deep), so a renamed `setup.exe` doesn't get through. Disk images (`.iso`, `.dmg`) and macro-enabled Office files count as programs too.
  - When programs are allowed, each one is stored inside a zip (`setup.exe.zip`) and downloads as that zip, so opening it can't run it. Settings warns when programs are allowed while **Allowed senders** is empty.
  - Text is extracted from PDF, DOCX, PPTX, HTML, EML and plain-text files for search and later summaries.
  - Download files from the email's page.
- **Label.** Adds `NOMAD/Processed` or `NOMAD/Failed` in Gmail. Both labels can be changed or turned off in Settings.

A failed email shows the reason on its page and has a **Reprocess** button. Ignored emails have **Process anyway**.

### 3. Links

A separate background worker, so slow summaries never hold up mail checks, handles each *Web* link in two steps:

- **Fetch.** Downloads the page (up to 10 MB, 45 s timeout). Addresses on your own network (`localhost`, `192.168.x.x`, NOMAD's own services, …) are refused unless **Allow links to local-network addresses** is on in Settings. The check happens when the connection is made, so redirects can't get around it.
- **Readable text.** For HTML, the article body is extracted the way a browser's Reader View does it, falling back to all visible text for pages that aren't articles. PDFs and plain-text links are read directly. Images, archives and other binary files are *Skipped*.
- **JavaScript pages.** If an HTML page yields under 500 characters (typical of single-page and WebAssembly apps that only send a loading shell), it's opened in headless Chromium through Playwright:
  - It waits for the network to go quiet and the visible text to stop changing (up to about a minute), then extracts the text again. The version with more text wins.
  - The local-network rule applies to every request the page makes, and images, fonts and media aren't loaded.
  - It can be turned off in Settings. The browser is part of the Docker image.
- **Summary.** The text is sent to NOMAD's Ollama with the email's category, tags and title as context. The answer is a bold one-line overview followed by key-point bullets. Pages too long for the context window are condensed part by part, then summarized together.

If Ollama isn't installed, isn't running, or no model is chosen yet, links wait as *Waiting for AI summary* and are summarized once it's available (checked every 2 minutes and whenever Settings are saved). A summary that errors is retried twice, then marked *Failed*. Each link has **Retry** or **Summarize again** on the email's page. To set up the AI, see [AI summaries](README-SETUP.md#ai-summaries).

### 4. YouTube

YouTube links go through the same fetch → summarize steps, using [yt-dlp](https://github.com/yt-dlp/yt-dlp). It's installed in the image and updates itself daily, and uses Playwright's bundled Node.js to solve YouTube's JavaScript challenges.

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
  - Videos also play on the email's page. A channel followed from a `#download` email downloads its new videos too. The container creates the folder and makes it writable for the app on start; with install.sh, set `NOMADINBOX_VIDEO_DIR` in `.env` to use another folder.

### 5. NOMAD knowledge base

A background sync puts everything into the knowledge base of NOMAD's AI Assistant, the documents its chat answers from, using the Command Center's API (`/api/rag/...`, no login needed on the local network). What goes in:

- **Each summarized link** (web page, video, channel) as a Markdown document: title, source, site or channel, date, length, category and tags, the AI summary, chapters, and the full article or transcript. The full text is what makes answers specific; it can be turned off.
- **Each stored attachment** NOMAD can read itself (PDF, Word, EPUB, text, images) as the original file. Other files with extracted text (PowerPoint, forwarded emails) go in as Markdown.
- **An email note** when the email body has text of its own beyond links.

How it's filed and kept in step:

- **Collections.** Documents go into a collection named after the item's top-level category (`Survival/Water` → `survival`), so whole areas can be switched on or off in NOMAD's chat. Uncategorized items go into `nomad inbox`.
- **Reconciliation.** The sync compares what *should* be in NOMAD with what NOMAD Inbox has sent: it uploads new documents, replaces changed ones (e.g. after **Summarize again**), and removes documents whose item was deleted. It runs after changes and every 2 minutes, sends at most 10 uploads per run, and shows NOMAD's embedding state (*Embedding…* → *Indexed · N chunks*) on each item's page.
- **Controls.** **Resend** (per item) and **Resend everything** (Settings) send again. If NOMAD isn't reachable, it simply waits.
- **Deleting.** **Delete…** on an item's page removes it with its links and attachments, and optionally its files: attachments and saved videos, unless another item uses them. Its documents are removed from NOMAD on the next sync, and its Message-ID is remembered, so a folder rescan or channel check won't bring it back.
- **Links back.** With **NOMAD Inbox address, for links** set in Settings (it's filled in from the address you open Settings with), every document links back to its email in NOMAD Inbox, image cards to the image, and saved pages to the saved copy. NOMAD's chat shows these links when its answer repeats them; its *Sources* list shows titles only. Names sent to NOMAD are plain ASCII.

### 6. Saved pages

A web link can also get a local copy of the page. This happens for emails tagged `#archive` (**Settings → Saved pages** can switch it to *Always* or *Never*), and anytime through **Save page** on an email's link.

- **Content.** The page's main content is extracted the way a browser's Reader View does it, or from a headless-browser render for JavaScript-built pages. Then it's cleaned with an allowlist: paragraphs, headings, **bold**/*italic* and other inline formatting, lists, quotes, code, tables and figures stay; scripts, iframes, forms, inline styles and classes go, so the look comes only from the local stylesheet.
- **Media.** Images (including lazy-loaded ones and the largest `srcset` size) and linked video files are downloaded within the size limits in Settings. Each is stored once, by content hash, and the page points at the local copy. Anything that couldn't be saved becomes a link to the original.
- **YouTube embeds** are added to the email as YouTube links. They get a transcript and summary, and are downloaded per the YouTube settings (`#download` or *Always*). Once downloaded, the saved page plays the video locally.
- **Links.** Every link in the content is recorded, and how it's shown is decided when the page is viewed. A link to a page that's saved too opens the local copy; one that isn't leads to a *Not saved yet* page with **Save this page**. Saving a page later updates the links in older pages. YouTube links saved this way go to the email as videos.
- **Viewing.** The **Library** (menu) lists saved pages, with search across title, site, address and text. A page opens full-width with a bar on top: Library, Email, Original, **Save linked pages** (same site, up to the limit in Settings), **Refresh**, **Delete**. Saved pages are served with a Content-Security-Policy that allows no scripts at all.
- **Template.** **Settings → Saved pages → Edit the page template** opens an editor for the [Scriban](https://github.com/scriban/scriban/blob/master/doc/language.md) HTML template and the stylesheet, with a live preview using one of your saved pages, syntax checking, and **Reset to default**. The variables are listed in a comment at the top of the template. A change applies to every saved page.
- **NOMAD's File Browser.** Saved pages are also written as plain HTML to `/opt/project-nomad/storage/filebrowser/files/nomad-inbox-pages`, which File Browser shows as `nomad-inbox-pages`:
  - `index.html` lists the pages by category, and each page has its own folder, `pages/<id>-<title>/index.html`, sharing `assets/` and `style.css`.
  - Links between saved pages are relative, so the folder also works when copied elsewhere and opened in a browser.
  - The images and videos live only here, and the app serves them from here, so nothing is stored twice. `NOMADINBOX_PAGES_DIR` changes the folder.

### 7. Image descriptions

NOMAD's Ollama looks at image attachments and at images on saved pages (the first 10 per page by default, without icons and logos) and describes each one, in the background and one at a time:

- **What it writes.** A caption of one or two sentences, the image's type (photo, diagram, map, chart, screenshot, document scan, illustration or other), up to 3 categories chosen from the ones you already use in NOMAD Inbox, up to 8 tags, and the text it can read in the image.
- **Model.** The summary model is used if it can read images (`gemma3:4b` and up can); **Settings → AI summaries → Model for images** picks another one, and **Check** asks Ollama whether a model can read images.
- **Formats.** JPG, PNG, WebP, GIF and BMP. SVG and HEIC are listed as skipped. Each picture is described once, by content, even when several emails or pages use it.
- **Where it shows.** Under each image attachment on the email's page (with **Redescribe**), as the alt text of images in saved pages, and in Library search.
- **Knowledge base.** A described image attachment goes into NOMAD's knowledge base as an *image card* (a Markdown document with all of the above and links to the image and the email) instead of the image file. A saved page's document gets an **Images** section listing its described images; it waits until the page's images are described (at most an hour), so NOMAD gets it once instead of again after each image.
- **Existing images.** Images that arrive after the feature is turned on are described automatically; **Describe existing images** in Settings describes the ones that were already there.

If Ollama isn't running, images wait and are described once it's back. A description that fails is retried twice, then marked failed.

### 8. Air-gapped operation

For a NOMAD without internet, **Settings → Mode** splits the work over two machines (set-up: [Air-gapped NOMAD](README-SETUP.md#air-gapped-nomad-a-collector-and-bundles)):

- **Collector** (a wired machine): checks mail and fetches everything as above, but instead of sending to NOMAD it writes **bundles**: ZIP files with each new or changed item (its links with text, transcripts and any summaries, its attachments and image descriptions, its saved pages with their images and videos, and its downloaded YouTube videos) and a manifest with a checksum for every file. Bundles are written on a schedule or with **Create bundle now**, and listed on the **Export** page. An item waits until its pages and videos are saved and, with an AI set up, its summaries are written (up to a few hours) before it goes into a bundle. A big export is split into several bundles, each under a size limit (3500 MB by default, so they fit a FAT32 USB drive) and each complete on its own.
- **Air-gapped** (the NOMAD): doesn't check mail or fetch anything. Bundles are imported on the **Import** page (or from a watched folder) after checking every file against the manifest; then this side's AI summarizes and describes whatever the collector didn't, and the items go to NOMAD's knowledge base like any others. Saved pages appear in the **Library** and NOMAD's File Browser, videos in the videos folder (Jellyfin). Imports are idempotent and order-independent, and items deleted on this side stay deleted.

Bundles aren't signed yet.

## Updating

Installed from NOMAD's Supply Depot, use **Manage › Update (pull latest)** on the app's card. NOMAD downloads the image's tag again and restarts the app with your data; if the new version doesn't start, it goes back to the one that was running. Database migrations run on start.

That's why the image is `:latest`: every release publishes `latest` (see [Releasing a version](README-DEV.md#releasing-a-version)), and for custom apps NOMAD's Update only re-downloads the tag the app is set to. With a pinned tag such as `:1.0.4`, Update just fetches 1.0.4 again; to move to another version you'd change the tag in **Manage › Edit**. The **Update available** pill, the version choice and automatic updates are for NOMAD's own catalog apps only, so they don't appear for NOMAD Inbox, and its card shows `latest` instead of a version number.

To go back to an older version, set the image to that version's tag in **Manage › Edit**, e.g. `ghcr.io/alonazar/nomad-inbox:1.0.2` ([all versions](https://github.com/alonazar/growmad/pkgs/container/nomad-inbox)). The database isn't migrated back, so only do this between versions with the same database.

Installed with install.sh, run the install command again; see [Run with install.sh instead](README-SETUP.md#run-with-installsh-instead-ubuntu--wsl2).
