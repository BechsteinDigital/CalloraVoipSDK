#!/usr/bin/env bash
# prepare-release.sh — deterministic half of the release preparation (see the sdk-release skill).
#
# Does ONLY the mechanical, unambiguous edits and a readiness scan; it never commits and never touches
# prose (CHANGELOG bodies, RELEASE_NOTES, docs narrative) — those are the sdk-release skill's job, because
# they need judgment. Run this first, then let the skill finish the prose + verification + PR.
#
#   Usage: scripts/prepare-release.sh <new-version> [iso-date]
#          <new-version>  X.Y.Z (SemVer, no leading v)
#          [iso-date]     defaults to today (UTC, YYYY-MM-DD)
#
# What it edits deterministically:
#   - src/Directory.Build.props            <VersionPrefix>
#   - README.md                            latest-release + release-notes references
#   - docs/portal/index.md                 "Latest release: vX.Y.Z"
#   - docs/reference/semver-policy.md      "aktuell `X.Y.Z`"
#   - docs/reference/README.md             "current version `X.Y.Z`"
#   - MAINTAINING.md                       the VersionPrefix example line only (never the dated Nachtrag)
#   - docs/portal/versions.json            latest + versions[] roll
#
# What it does NOT touch (skill/human judgment): CHANGELOG.md, RELEASE_NOTES_X.Y.Z.md, the new MAINTAINING
# Nachtrag, docs/portal/changelog.md + status prose, docs/portal/interop/* matrix rows.
set -euo pipefail

repo_root="$(git rev-parse --show-toplevel)"
cd "$repo_root"

NEW="${1:-}"
DATE="${2:-$(date -u +%F)}"
if [[ ! "$NEW" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "usage: $0 <new-version X.Y.Z> [iso-date]" >&2
  exit 2
fi
MM="${NEW%.*}"   # major.minor, e.g. 4.12

props="src/Directory.Build.props"
OLD="$(grep -oPm1 '(?<=<VersionPrefix>)[0-9]+\.[0-9]+\.[0-9]+(?=</VersionPrefix>)' "$props")"
OLD_MM="${OLD%.*}"
if [[ -z "$OLD" ]]; then echo "could not read current VersionPrefix from $props" >&2; exit 1; fi
if [[ "$OLD" == "$NEW" ]]; then echo "current version is already $NEW — nothing to bump" >&2; exit 1; fi

echo "== prepare-release: $OLD -> $NEW  (date $DATE) =="

# ── readiness scan (report-only; the skill acts on it) ──────────────────────────────────────────────
last_tag="$(git describe --tags --abbrev=0 2>/dev/null || true)"
echo
echo "-- readiness scan (baseline: ${last_tag:-<no tag>}) --"
if [[ -n "$last_tag" ]]; then
  echo "commits since $last_tag:"
  git log --no-merges --format='  %s' "${last_tag}..HEAD"

  echo
  if git diff --name-only "${last_tag}..HEAD" | grep -q 'PublicApi.*approved'; then
    echo "  SemVer hint: PublicApi.approved.txt CHANGED since $last_tag."
    echo "    -> additions only  => MINOR;  removals / signature changes => MAJOR. Verify the diff:"
    echo "       git diff ${last_tag}..HEAD -- '*PublicApi*approved*'"
  else
    echo "  SemVer hint: PublicApi.approved.txt UNCHANGED => no public-surface break; PATCH or MINOR by behaviour."
  fi

  echo
  echo "  CHANGELOG coverage — feat/fix commits since $last_tag vs the [Unreleased] section:"
  unreleased="$(awk '/^## \[Unreleased\]/{f=1;next} /^## \[/{f=0} f' CHANGELOG.md)"
  git log --no-merges --format='%s' "${last_tag}..HEAD" \
    | grep -iE '^(feat|fix)' | while IFS= read -r subj; do
        ref="$(grep -oE '#[0-9]+' <<<"$subj" | head -1 || true)"
        if [[ -z "$ref" ]]; then
          echo "    CHECK $subj  -> no #ref to match; confirm a [Unreleased] entry by hand"
        elif grep -qF "$ref" <<<"$unreleased"; then
          echo "    ok    $subj  ($ref in [Unreleased])"
        else
          echo "    MISS  $subj  -> add a [Unreleased] entry ($ref)"
        fi
      done
fi

# ── deterministic edits ─────────────────────────────────────────────────────────────────────────────
echo
echo "-- deterministic bumps --"
bump() {  # bump <file> <sed-expr...>  (portable in-place)
  local f="$1"; shift
  [[ -f "$f" ]] || { echo "    skip (missing) $f"; return; }
  local before; before="$(cat "$f")"
  perl -0pi -e "$@" "$f"
  if [[ "$before" != "$(cat "$f")" ]]; then echo "    bumped $f"; else echo "    no-op  $f (pattern not found — check by hand)"; fi
}

bump "$props"                       "s{<VersionPrefix>\Q$OLD\E</VersionPrefix>}{<VersionPrefix>$NEW</VersionPrefix>}g"
bump "docs/portal/index.md"         "s{Latest release: \*\*v\Q$OLD\E\*\*}{Latest release: **v$NEW**}g"
bump "docs/reference/semver-policy.md" "s{aktuell \`\Q$OLD\E\`}{aktuell \`$NEW\`}g"
bump "docs/reference/README.md"     "s{current version \`\Q$OLD\E\`}{current version \`$NEW\`}g"
# README: the latest-release line + the RELEASE_NOTES link both point at the current release.
bump "README.md"                    "s{\Q$OLD\E}{$NEW}g"
# MAINTAINING: only the VersionPrefix example (anchored on 'Releases überschreiben'); never the dated Nachtrag.
bump "MAINTAINING.md"               "s{\`\Q$OLD\E\`\); Releases überschreiben}{\`$NEW\`); Releases überschreiben}g"

# versions.json roll: previous latest (path \"\") gets its numbered path; new label prepended as latest.
vj="docs/portal/versions.json"
if [[ -f "$vj" ]] && command -v jq >/dev/null 2>&1; then
  tmp="$(mktemp)"
  jq --arg new "$MM" --arg old "$OLD_MM" '
    .latest = $new
    | .versions = ([{label:$new, path:""}]
        + (.versions | map(if .label==$old and .path=="" then .path=$old else . end)))
  ' "$vj" > "$tmp" && mv "$tmp" "$vj"
  echo "    bumped $vj (latest -> $MM; $OLD_MM path -> \"$OLD_MM\")"
else
  echo "    skip  $vj (jq missing or file absent — update by hand)"
fi

# ── the judgment checklist the skill/human must still do ─────────────────────────────────────────────
cat <<EOF

-- still TODO (judgment — the sdk-release skill owns these) --
  [ ] CHANGELOG.md: roll '## [Unreleased]' -> '## [$NEW] - $DATE', add a fresh empty [Unreleased] scaffold,
      and make sure EVERY feat/fix flagged MISS above has an entry (with the behaviour-notes, not just adds).
  [ ] RELEASE_NOTES_$NEW.md: new file — the narrative for this release.
  [ ] README.md line ~64: confirm the RELEASE_NOTES_$NEW.md link resolves (the file must exist).
  [ ] MAINTAINING.md: add a new dated '> **Nachtrag $NEW ($DATE):**' entry (do NOT edit the old one).
  [ ] docs/portal/changelog.md + docs/portal/index.md status prose: reflect the new release.
  [ ] docs/portal/interop/*: any matrix rows this release changes (honest status only).
  [ ] Verify: dotnet build CalloraVoipSdk.sln -c Release -warnaserror (all TFMs) + dotnet test (arch + suites).
  [ ] SemVer sanity vs the PublicApi hint above.
EOF
echo
echo "== done: files edited, NOT committed. Review 'git diff', finish the checklist, then open the release PR. =="
