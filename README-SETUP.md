# NOMAD Inbox: setup

How to install NOMAD Inbox on a [Project NOMAD](https://github.com/Crosstalk-Solutions/project-nomad) server and connect it to Gmail. For what it does, see the [README](README.md); for development, see [README-DEV](README-DEV.md).

- [Install from NOMAD's Supply Depot](#install-from-nomads-supply-depot-recommended) (recommended)
- [Gmail setup](#gmail-setup)
- [AI summaries](#ai-summaries)
- [Move an install made with install.sh](#move-an-install-made-with-installsh)
- [Run with install.sh instead](#run-with-installsh-instead-ubuntu--wsl2)
- [Air-gapped NOMAD: a collector and bundles](#air-gapped-nomad-a-collector-and-bundles)

## Install from NOMAD's Supply Depot (recommended)

NOMAD can run NOMAD Inbox as a custom app, next to its own apps: it gets an **Open** button, and **Manage** gives you Edit, Logs, Stats, Update and Remove. App settings (Gmail, AI, Appearance, …) stay on NOMAD Inbox's own Settings page.

In NOMAD's Command Center, open the **Supply Depot**, click **Add a custom app**, and fill in:

| Field | Value |
|---|---|
| Docker Image | `ghcr.io/alonazar/nomad-inbox:latest` (NOMAD warns that `:latest` is a moving tag; tick **install anyway**, see [Updating](README.md#updating)) |
| Display Name | `NOMAD Inbox` |
| Port | container `8080` → a free host port, e.g. `8601` |
| Volumes | `/opt/project-nomad/storage/nomad-inbox` → `/data` (database and keys: back this up) |
| | `/opt/project-nomad/storage/media/youtube` → `/videos` (saved videos, seen by Jellyfin and File Browser) |
| | `/opt/project-nomad/storage/filebrowser/files/nomad-inbox-pages` → `/pages-export` (saved pages, seen in File Browser) |
| Environment | `Auth__Password=<a password for the web UI>` (sign in as `admin`) and `TZ=<your time zone>`, e.g. `TZ=America/New_York` |
| Memory / CPUs | `2048` MB and `2` (the page renderer and video downloads need more than the 1 GB default) |

NOMAD downloads the image and starts it; the container creates its folders itself. Then click **Open** and continue with [Gmail setup](#gmail-setup). The AI and knowledge base addresses already point at NOMAD (`nomad_ollama`, `nomad_admin`).

To change the password or time zone later, use **Manage › Edit**. To update, see [Updating](README.md#updating).

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

Open NOMAD Inbox's **Settings** and sign in as `admin`: from the Supply Depot, click **Open**; with install.sh, open `http://localhost:8150/settings`. The password is the `Auth__Password` you gave the Supply Depot, or for install.sh, run this in Ubuntu:

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

If it shows as **Ignored**, the sender address isn't on your allowlist exactly as Gmail reports it. The subject line is explained in [Subject syntax](README.md#subject-syntax).

## AI summaries

1. In NOMAD's Command Center, install the **AI Assistant** (it runs Ollama in the `nomad_ollama` container on port 11434).
2. In the AI Assistant's settings, download a model. On a 16 GB GPU, `gemma3:12b` is a good balance of quality and room for long pages. Smaller GPUs can use `gemma3:4b` or `qwen3:8b`.
3. In NOMAD Inbox → **Settings → AI summaries**:
   - leave the address as `http://nomad_ollama:11434`
   - click **Load models** and choose the model
   - click **Test the model**, then **Save settings**

The summary instructions can be changed under **Summary instructions (advanced)**.

**Image descriptions** use the same model when it can read images (`gemma3:12b` can). Under **Image descriptions**, click **Check** to confirm, or enter a vision model under **Model for images**. Click **Describe existing images** once to describe the images you already have.

For links from NOMAD's chat back to NOMAD Inbox, check **NOMAD knowledge base → NOMAD Inbox address, for links**: it's filled in from the address you opened Settings with (e.g. `http://localhost:8601`); click **Save settings** to use it.

## Move an install made with install.sh

Your emails, settings and the stored Gmail app password move along; saved videos and pages are already in NOMAD's folders. In Ubuntu:

1. Note your admin password and time zone, then stop the old app. **Never run both at once**: both would check Gmail and process the same mail.
   ```bash
   grep -E '^(NOMADINBOX_ADMIN_PASSWORD|TZ)=' ~/nomad-inbox/.env
   cd ~/nomad-inbox && docker compose stop
   ```
2. Copy the data volume (database, `keys/`, `files/`, `pages/`) into NOMAD's storage, using the NOMAD Inbox image itself:
   ```bash
   docker volume ls | grep nomad-inbox   # usually nomad-inbox_nomad-inbox-data
   sudo mkdir -p /opt/project-nomad/storage/nomad-inbox
   docker run --rm --entrypoint cp -v nomad-inbox_nomad-inbox-data:/from:ro \
     -v /opt/project-nomad/storage/nomad-inbox:/to ghcr.io/alonazar/nomad-inbox:latest -a /from/. /to/
   ```
3. Add the custom app [as above](#install-from-nomads-supply-depot-recommended), with the same password and time zone. Open it and check your emails, Settings (**Test connection**) and Library.
4. When all is well, remove the old containers. Without `-v` the old volume stays as a backup; delete it later with `docker volume rm nomad-inbox_nomad-inbox-data`.
   ```bash
   cd ~/nomad-inbox && docker compose down
   ```

To go back before step 4: remove the custom app in the Supply Depot, then `cd ~/nomad-inbox && docker compose up -d`.

After the move, don't run `install.sh` or `docker compose up` in `~/nomad-inbox` again: either would start a second copy next to the Supply Depot app.

## Run with install.sh instead (Ubuntu / WSL2)

Without NOMAD's Supply Depot, or to run a branch. Requires Docker, which the NOMAD installer already set up. One command installs NOMAD Inbox, and the same command updates it later.

Run, as your normal user (not root):

```bash
curl -fsSL https://raw.githubusercontent.com/alonazar/growmad/main/install.sh | bash
```

The [install script](install.sh):
- checks the system (Ubuntu/Debian, Docker with compose) and installs `git`, `curl` and `openssl` if missing
- downloads the code into `~/nomad-inbox` with `git`
- creates `.env` with a generated admin password, the server's time zone and a free port (8150, or the next free one)
- builds and starts the containers, waits until NOMAD Inbox answers, and prints the address and login

Run again, the same command updates to the latest `main`. It keeps `.env` and all data, and stops if tracked files were changed locally. Options are environment variables, documented at the top of the script:
- `NOMADINBOX_DIR`: install folder
- `NOMADINBOX_BRANCH`: branch to install
- `NOMADINBOX_PORT`: web port for a new install
- `NOMADINBOX_REPO`: repository to install from

If Project NOMAD isn't installed on the machine, saved videos go to `~/nomad-inbox/videos`, and AI summaries and the knowledge base stay off until NOMAD's AI Assistant is available.

Installing from a private fork works too: give a fine-grained GitHub token with **Contents: Read-only** access to that repository. It's used for that run only and never written to disk:

```bash
export GITHUB_TOKEN=github_pat_...
curl -fsSL -H "Authorization: Bearer $GITHUB_TOKEN" https://raw.githubusercontent.com/<owner>/<repo>/main/install.sh \
  | NOMADINBOX_REPO=https://github.com/<owner>/<repo>.git bash
```

Open `http://<host>:8150` (from Windows with WSL2: `http://localhost:8150`). Port 8150 avoids NOMAD's own apps (8080, 8090, 8100-8500, 9999, 11434).

Data lives in the `nomad-inbox-data` volume:
- the database
- `keys/`, which decrypts the stored app password (back it up together with the database)
- `files/`, the filed attachments

To update to the latest version on GitHub, run the install command again, or do it by hand. Your `.env` and data are kept, and database migrations run automatically on start:

```bash
cd ~/nomad-inbox && git pull && docker compose up -d --build
```

**WSL note:** WSL shuts down idle distros, which stops polling. Keep the distro running, for example by starting it at Windows login, or by setting `vmIdleTimeout=-1` under `[wsl2]` in `%UserProfile%\.wslconfig`.

## Air-gapped NOMAD: a collector and bundles

For a NOMAD without internet. NOMAD Inbox then runs twice:

- a **collector** on any wired machine (no NOMAD needed): it checks Gmail, fetches pages, transcripts and videos, and writes **bundles** (ZIP files);
- NOMAD Inbox on the **air-gapped NOMAD**: it imports the bundles you carry over on a USB drive, then summarizes what the collector didn't and fills NOMAD's knowledge base as usual.

Where the AI runs: if the collector has an AI set up (**Settings → AI summaries**, e.g. NOMAD's AI Assistant next to it), links are summarized and images described there, and bundles carry the results. Otherwise the air-gapped NOMAD's AI does it after the import.

### The collector

1. Install with [install.sh](#run-with-installsh-instead-ubuntu--wsl2) on the wired machine and do the [Gmail setup](#gmail-setup).
2. **Settings → Mode → Collector**. Give it a name (shown on the air-gapped side), how often to write a bundle (every 24 hours by default; 0 = only by hand), how long an item waits for the collector's AI, and the largest bundle.
3. Bundles appear on the **Export** page (download them there) and in `~/nomad-inbox/export`. **Create bundle now** writes one straight away. Each bundle holds only items that are new or changed since the last one, with their attachments, saved pages and downloaded videos.
4. **USB drives:** a FAT32 drive (most USB sticks) takes files up to 4 GB, so a bigger export is split into several bundles of at most **Largest bundle** (3500 MB by default). Copy all of them; they can be imported in any order. An item is never split, so one with a video bigger than the limit gets a bundle of its own: keep **Settings → YouTube → Max size per video** under 4000 MB (it's 2048 by default), or use an exFAT drive.

### The air-gapped NOMAD

1. **Bring the image.** The air-gapped NOMAD can't download it, so save it on a wired machine and load it there:
   ```bash
   docker pull ghcr.io/alonazar/nomad-inbox:1.0.11
   docker save ghcr.io/alonazar/nomad-inbox:1.0.11 | gzip > nomad-inbox-1.0.11.tar.gz   # copy this to the USB drive
   ```
   On the NOMAD host: `gunzip -c nomad-inbox-1.0.11.tar.gz | docker load`. Use the version you want instead of `1.0.11`.
2. [Add the custom app](#install-from-nomads-supply-depot-recommended) as usual, but with that version as the image (e.g. `ghcr.io/alonazar/nomad-inbox:1.0.11`, not `:latest`): NOMAD finds it locally and doesn't try to download it.
3. Open it, **Settings → Mode → Air-gapped**, and set up [AI summaries](#ai-summaries). No Gmail setup is needed.
4. To import, open **Import**, choose the bundle files from the USB drive, and click **Import**.
   - Importing the same bundle twice, or bundles out of order, is fine; a newer copy of an item is never replaced by an older one.
   - Items you delete here stay deleted, even when a later bundle carries them again.
   - A bundle that isn't complete or was changed is rejected, with the reason on the Import page.
   - Saved pages go to the **Library** and NOMAD's File Browser (`nomad-inbox-pages`), videos to NOMAD's media folder for Jellyfin, as on a connected NOMAD. Here, pages can't be refreshed or saved from their links: that happens on the collector.
5. **Optional, a watched folder:** in **Manage › Edit**, add the volume `/opt/project-nomad/storage/filebrowser/files/nomad-inbox-import` → `/import` and the environment variable `NomadInbox__ImportDirectory=/import`, then turn on **Settings → Mode → Also import bundles placed in the import folder**. Bundles copied into `nomad-inbox-import` in NOMAD's File Browser are imported within a minute and moved to `imported` (or `rejected`).

**Updating** the air-gapped NOMAD Inbox works the same way: `docker load` the new version, then change the image tag in **Manage › Edit**. (**Update (pull latest)** needs the internet.) Update it before or together with the collector: a bundle from a newer collector can need a newer NOMAD Inbox, and the Import page says so if it does.

Bundles aren't signed yet; signing comes next.
