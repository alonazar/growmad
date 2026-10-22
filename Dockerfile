# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy everything before restoring. Restoring from the .csproj files alone (the usual caching trick) skips
# the package that provides _framework/blazor.web.js, because the SDK only adds it when .razor files exist.
# Without that script no page is interactive.
COPY src/ src/
RUN dotnet publish src/NomadInbox.Web/NomadInbox.Web.csproj -c Release -o /app/publish
RUN test -f /app/publish/wwwroot/_framework/blazor.web.js \
    || (echo "ERROR: _framework/blazor.web.js is missing from the publish output" >&2 && exit 1)

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

# tzdata so TZ from compose.yaml controls the times shown in the UI; ffmpeg so yt-dlp can merge YouTube's
# separate video and audio streams into one MP4 when saving videos.
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata ffmpeg \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data /videos /pages-export \
    && chown "$APP_UID" /data /videos /pages-export

COPY --from=build /app/publish .

# Headless Chromium (plus the OS libraries it needs) for pages built by JavaScript. Installed as root into a
# shared path so the non-root app user can run it.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
# The Playwright driver is copied in as root with mode 744; a+rX makes it runnable by the app user too.
RUN dotnet NomadInbox.Web.dll install-browser \
    && rm -rf /var/lib/apt/lists/* \
    && chmod -R a+rX /ms-playwright /app/.playwright

# yt-dlp for YouTube transcripts and channel listings: the standalone build (no Python needed), owned by the
# app user so it can update itself daily (YouTube changes often). It uses Playwright's Node.js for YouTube's
# JavaScript challenges.
ADD --chmod=755 https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_linux /opt/yt-dlp/yt-dlp
RUN chown -R "$APP_UID" /opt/yt-dlp

ENV ASPNETCORE_HTTP_PORTS=8080 \
    NomadInbox__DataDirectory=/data \
    NomadInbox__VideoDirectory=/videos \
    NomadInbox__PagesExportDirectory=/pages-export

USER $APP_UID
# Fail the build if the app user can't actually start the browser, yt-dlp or ffmpeg.
RUN dotnet NomadInbox.Web.dll check-browser && /opt/yt-dlp/yt-dlp --version && ffmpeg -hide_banner -version | head -1
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "NomadInbox.Web.dll"]
