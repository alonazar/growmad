#!/usr/bin/env bash
# NOMAD Inbox installer and updater for Ubuntu (and other Debian-based Linux, including WSL2).
#
# Run the same command to install or to update; an update keeps your .env (admin password, settings) and all data.
#
#   Public repository:
#     curl -fsSL https://raw.githubusercontent.com/alonazar/growmad/main/install.sh | bash
#
#   Private repository: create a fine-grained GitHub token with read-only "Contents" access to this repository, then:
#     export GITHUB_TOKEN=github_pat_...
#     curl -fsSL -H "Authorization: Bearer $GITHUB_TOKEN" \
#       https://raw.githubusercontent.com/alonazar/growmad/main/install.sh | bash
#   The token is only used for this run; it is never written to disk.
#
# Options (environment variables):
#   NOMADINBOX_DIR     install folder                  (default: ~/nomad-inbox)
#   NOMADINBOX_BRANCH  branch to install               (default: main)
#   NOMADINBOX_REPO    repository URL or local path    (default: https://github.com/alonazar/growmad.git)
#   NOMADINBOX_PORT    web UI port for a new install   (default: 8150, or the next free port)
#
# Needs Docker with the compose plugin (the Project NOMAD installer sets this up), plus git, curl and openssl
# (installed with apt if missing).

set -euo pipefail

REPO="${NOMADINBOX_REPO:-https://github.com/alonazar/growmad.git}"
BRANCH="${NOMADINBOX_BRANCH:-main}"
DIR="${NOMADINBOX_DIR:-$HOME/nomad-inbox}"
NOMAD_STORAGE="/opt/project-nomad/storage"

if [ -t 1 ]; then BOLD=$'\e[1m'; GREEN=$'\e[32m'; YELLOW=$'\e[33m'; RED=$'\e[31m'; RESET=$'\e[0m'
else BOLD=""; GREEN=""; YELLOW=""; RED=""; RESET=""; fi
step() { printf '\n%s==> %s%s\n' "$BOLD" "$*" "$RESET"; }
info() { printf '    %s\n' "$*"; }
warn() { printf '%s    ! %s%s\n' "$YELLOW" "$*" "$RESET"; }
die()  { printf '\n%sError: %s%s\n' "$RED" "$*" "$RESET" >&2; exit 1; }

SUDO=""
DOCKER=(docker)
GIT_AUTH=()

check_system() {
  step "Checking the system"
  [ -r /etc/os-release ] || die "Can't identify this Linux distribution (no /etc/os-release)."
  # shellcheck disable=SC1091
  . /etc/os-release
  case " ${ID:-} ${ID_LIKE:-} " in
    *" ubuntu "*|*" debian "*) info "${PRETTY_NAME:-$ID}" ;;
    *) die "This installer supports Ubuntu and other Debian-based systems; found ${PRETTY_NAME:-unknown}." ;;
  esac
  if [ "$(id -u)" -ne 0 ]; then
    command -v sudo >/dev/null || die "sudo is needed to install missing packages."
    SUDO="sudo"
  fi
}

install_packages() {
  local missing=()
  for cmd in git curl openssl; do command -v "$cmd" >/dev/null || missing+=("$cmd"); done
  if [ ${#missing[@]} -gt 0 ]; then
    step "Installing ${missing[*]}"
    $SUDO apt-get update -qq
    $SUDO apt-get install -y -qq "${missing[@]}" ca-certificates >/dev/null
  fi
}

check_docker() {
  step "Checking Docker"
  command -v docker >/dev/null || die "Docker isn't installed. Install Project NOMAD first (it sets up Docker), or run:
    curl -fsSL https://get.docker.com | sudo sh"
  if docker info >/dev/null 2>&1; then
    DOCKER=(docker)
  elif [ -n "$SUDO" ] && sudo docker info >/dev/null 2>&1; then
    DOCKER=(sudo docker)
    warn "Using sudo for Docker. To avoid that: sudo usermod -aG docker \$USER (then log in again)."
  else
    die "Docker is installed but not running or not reachable. Start it with: sudo systemctl start docker"
  fi
  "${DOCKER[@]}" compose version >/dev/null 2>&1 || die "The Docker compose plugin is missing (sudo apt-get install docker-compose-plugin)."
  info "$("${DOCKER[@]}" --version)"
}

setup_git_auth() {
  # A token from the environment authenticates github.com for this run only (an HTTP header; nothing is stored).
  if [ -n "${GITHUB_TOKEN:-}" ] && [[ "$REPO" == https://github.com/* ]]; then
    local basic
    basic=$(printf 'x-access-token:%s' "$GITHUB_TOKEN" | base64 | tr -d '\n')
    GIT_AUTH=(-c "http.https://github.com/.extraheader=AUTHORIZATION: basic $basic")
  fi
}

git_remote() { GIT_TERMINAL_PROMPT=0 git "${GIT_AUTH[@]}" "$@"; }

fetch_code() {
  if [ -d "$DIR/.git" ]; then
    MODE=update
    step "Updating $DIR to the latest $BRANCH"
    if [ -n "$(git -C "$DIR" status --porcelain --untracked-files=no)" ]; then
      die "$DIR has local changes to tracked files. Commit or discard them (git -C $DIR status) and run again."
    fi
    local before
    before=$(git -C "$DIR" rev-parse --short HEAD)
    git_remote -C "$DIR" fetch -q origin "$BRANCH" || die "Couldn't fetch from $(git -C "$DIR" remote get-url origin). $(auth_hint)"
    git -C "$DIR" checkout -q -B "$BRANCH" "origin/$BRANCH"
    local after
    after=$(git -C "$DIR" rev-parse --short HEAD)
    if [ "$before" = "$after" ]; then info "Already up to date ($after)."; else info "$before -> $after"; fi
  elif [ -e "$DIR" ] && [ -n "$(ls -A "$DIR" 2>/dev/null)" ]; then
    die "$DIR exists and isn't a NOMAD Inbox checkout. Choose another folder with NOMADINBOX_DIR=..."
  else
    MODE=install
    step "Downloading NOMAD Inbox ($BRANCH) into $DIR"
    git_remote clone -q --branch "$BRANCH" "$REPO" "$DIR" || die "Couldn't download $REPO. $(auth_hint)"
    info "$(git -C "$DIR" log --oneline -1)"
  fi
}

auth_hint() {
  if [[ "$REPO" == https://github.com/* ]] && [ -z "${GITHUB_TOKEN:-}" ]; then
    echo "If the repository is private, set GITHUB_TOKEN to a token with read access (see the top of this script)."
  elif [ -n "${GITHUB_TOKEN:-}" ]; then
    echo "Check that GITHUB_TOKEN is valid and has read access to the repository."
  fi
}

port_in_use() { ss -ltnH 2>/dev/null | awk '{print $4}' | grep -Eq "[:.]$1\$"; }

detect_timezone() {
  local tz=""
  command -v timedatectl >/dev/null && tz=$(timedatectl show -p Timezone --value 2>/dev/null || true)
  [ -z "$tz" ] && [ -r /etc/timezone ] && tz=$(cat /etc/timezone)
  echo "${tz:-UTC}"
}

write_env() {
  ENV_FILE="$DIR/.env"
  if [ -f "$ENV_FILE" ]; then
    step "Keeping the existing settings in $ENV_FILE"
    NEW_PASSWORD=""
    return
  fi

  step "Creating $ENV_FILE"
  local port="${NOMADINBOX_PORT:-8150}"
  if [ -z "${NOMADINBOX_PORT:-}" ]; then
    while port_in_use "$port"; do port=$((port + 1)); done
    [ "$port" = 8150 ] || warn "Port 8150 is taken; using $port."
  elif port_in_use "$port"; then
    die "Port $port is already in use. Pick another with NOMADINBOX_PORT=..."
  fi

  NEW_PASSWORD=$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 24)
  local tz
  tz=$(detect_timezone)

  ( # private file: the subshell keeps the strict umask from leaking into the rest of the script
    umask 077
    {
      echo "# Created by install.sh on $(date -I). Keep this file private; it holds the web UI login."
      echo "NOMADINBOX_ADMIN_USER=admin"
      echo "NOMADINBOX_ADMIN_PASSWORD=$NEW_PASSWORD"
      echo "NOMADINBOX_PORT=$port"
      echo "TZ=$tz"
      if [ -n "${NOMADINBOX_VIDEO_DIR:-}" ]; then
        echo "NOMADINBOX_VIDEO_DIR=$NOMADINBOX_VIDEO_DIR"
      elif [ ! -d "$NOMAD_STORAGE" ]; then
        echo "# Project NOMAD wasn't found, so saved videos stay inside the install folder."
        echo "NOMADINBOX_VIDEO_DIR=$DIR/videos"
      fi
      # Only needed to run a second copy next to another one (e.g. for testing).
      [ -z "${NOMADINBOX_CONTAINER:-}" ] || echo "NOMADINBOX_CONTAINER=$NOMADINBOX_CONTAINER"
      [ -z "${NOMADINBOX_IMAGE:-}" ] || echo "NOMADINBOX_IMAGE=$NOMADINBOX_IMAGE"
    } > "$ENV_FILE"
  )
  info "Port $port, time zone $tz, admin password generated."
}

env_value() { grep -E "^$1=" "$ENV_FILE" | tail -1 | cut -d= -f2-; }

start() {
  step "Building and starting (the first build takes a few minutes)"
  (cd "$DIR" && "${DOCKER[@]}" compose up -d --build --remove-orphans)

  local port
  port=$(env_value NOMADINBOX_PORT)
  port="${port:-8150}"
  step "Waiting for NOMAD Inbox to answer on port $port"
  for _ in $(seq 1 90); do
    if curl -fs "http://localhost:$port/health" >/dev/null 2>&1; then info "Healthy."; PORT="$port"; return; fi
    sleep 2
  done
  die "NOMAD Inbox didn't come up. See the logs: cd $DIR && ${DOCKER[*]} compose logs --tail 50"
}

summary() {
  local ip
  ip=$(hostname -I 2>/dev/null | awk '{print $1}')
  printf '\n%s%s NOMAD Inbox is %s.%s\n\n' "$GREEN" "$BOLD" "$([ "$MODE" = install ] && echo installed || echo up to date)" "$RESET"
  info "Open:      http://localhost:$PORT${ip:+   (from other devices: http://$ip:$PORT)}"
  if [ -n "$NEW_PASSWORD" ]; then
    info "Login:     $(env_value NOMADINBOX_ADMIN_USER) / $NEW_PASSWORD"
    info "           (saved in $ENV_FILE; see it again with: grep PASSWORD $ENV_FILE)"
  else
    info "Login:     unchanged (grep PASSWORD $ENV_FILE)"
  fi
  info "Update:    run the same install command again"
  info "Logs:      cd $DIR && ${DOCKER[*]} compose logs -f"
  if [ "$MODE" = install ]; then
    info "Next:      Settings → connect the Gmail account (see README.md, \"Gmail setup\")"
  fi
  [ -d "$NOMAD_STORAGE" ] || warn "Project NOMAD wasn't found on this machine: AI summaries and the knowledge base need NOMAD's AI Assistant."
  if grep -qi microsoft /proc/version 2>/dev/null; then
    warn "WSL: polling stops when WSL shuts down an idle distro; set vmIdleTimeout=-1 under [wsl2] in %UserProfile%\\.wslconfig."
  fi
  echo
}

main() {
  check_system
  install_packages
  check_docker
  setup_git_auth
  fetch_code
  write_env
  start
  summary
}

# Everything runs from here, so a partly downloaded script (curl | bash) can't run halfway.
main "$@"
