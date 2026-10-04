# Releasing

A release is cut by pushing a version tag. `.github/workflows/release.yml` does the rest: it pushes the application image and the gateway image of that version to GHCR for `linux/amd64` and `linux/arm64`, and creates the GitHub release with the extension zip and its checksum attached. Nobody builds, pushes, or uploads anything by hand.

## Who can release

Anyone who holds the **admin role** on the repository, and nobody else. Today that is the owner alone. **Pushing the tag is the approval to publish**: there is no second approval, no reviewer, and no GitHub environment between the tag and the registry. The release workflow has no other trigger, so there is no other way in. To go back to an earlier version, see [Roll back](#roll-back).

Two tag rulesets enforce this, on `refs/tags/v*`, the same tags the release workflow starts on:

| Ruleset | ID | What it does | Who can bypass it |
| --- | --- | --- | --- |
| `release-tags-create` | _not applied yet_ | Refuses the creation of a `v*` tag. | The repository admin role, so only an admin can create one. |
| `release-tags-immutable` | _not applied yet_ | Refuses moving a `v*` tag to another commit and deleting it. | Nobody, the owner included. |

Their definitions are `.github/rulesets/release-tags-create.json` and `.github/rulesets/release-tags-immutable.json`, applied like the `main` ruleset with `scripts/apply-rulesets.sh` (see [Branch and tag rules](../README.md#branch-and-tag-rules)). To check that they exist and are what the files say:

```sh
gh api "repos/nathanpond/n8Tracks/rulesets?targets=tag"   # both rulesets, with their IDs
gh api repos/nathanpond/n8Tracks/rulesets/<ID>            # one ruleset: rules, conditions, bypass list
scripts/apply-rulesets.sh --check                         # changes nothing; reports any difference from the files
```

What follows from them:

- **A version tag is never moved and never reused.** Once `v1.4.2` is pushed it points at that commit for good; a forced push to it and a deletion are both refused, for everyone. A tag that is bad or points at the wrong commit is fixed by releasing the next patch version (`v1.4.3`), or the next pre-release number (`v1.5.0-rc.2`) for a pre-release.
- **A tag the release workflow refuses, or whose run cannot be made to pass, cannot be deleted afterwards. That version number is spent.** Nothing was published under it; bump `VERSION` to the next patch and tag that. So check the three things `plan` checks before pushing a tag: it is `v` plus the content of `VERSION`, at the tagged commit, and that commit is in `main`.
- **The rulesets do not protect against an admin.** Someone with the admin role can disable or delete either ruleset in the repository settings, and then move or delete a tag. For a project with one maintainer that is accepted: the rulesets stop mistakes and anyone with less than admin, not the owner acting deliberately. `scripts/apply-rulesets.sh --check` shows a ruleset that was disabled, changed, or removed.

## Cut a release

1. **Bump `VERSION` in a pull request.** Set the root `VERSION` file to the version being released: `1.4.2`, or `1.5.0-rc.1` for a pre-release (lower-case letters, digits, hyphens, and dots after the hyphen). Then run `npm run build` in `extension/`, which copies the version into `extension/package.json` and its lockfile, and commit all three files.
2. **Merge it** once `ci` is green.
3. **Tag the merged commit on `main` and push the tag.** The tag is the version with a `v` in front. This is the step that publishes, and it cannot be taken back: the tag can be neither moved nor deleted once pushed.

   ```sh
   git switch main && git pull
   git tag "v$(cat VERSION)"
   git push origin "v$(cat VERSION)"
   ```

4. **Watch the run**: `gh run watch "$(gh run list --workflow release.yml --limit 1 --json databaseId --jq '.[0].databaseId')"`. Its summary lists every image tag with its digest, and the address of the GitHub release.

After a pre-release, the next pull request moves `VERSION` on (to `1.5.0-rc.2`, or to `1.5.0` for the release itself). While `VERSION` holds a pre-release, `edge` builds report `<VERSION>.edge.<short sha>`.

## What a tag publishes

`scripts/release-tags.sh` decides, and its rules are at the top of that file. In short:

| Tag | Image tags, on both images | GitHub release |
| --- | --- | --- |
| `v1.4.2` | `1.4.2`; `1.4` unless a higher `1.4.x` is already published; `latest` unless a higher stable version is already published | named `v1.4.2`, marked latest only when the images got `latest`, notes generated from the previous stable tag |
| `v1.5.0-rc.1` | `1.5.0-rc.1` only | named `v1.5.0-rc.1`, marked as a pre-release, never latest, notes generated from the previous tag of any kind |

Either way the release carries `n8tracks-extension-<version>.zip` and `n8tracks-extension-<version>.zip.sha256` (check a download with `sha256sum -c n8tracks-extension-<version>.zip.sha256`). The images and the extension report exactly the tagged version, and the images carry build provenance and an SBOM as attestations, like the [`edge` images](../README.md#edge-images).

The run has three jobs, in order:

1. `plan` runs the release rules. The tag is **refused**, and nothing else starts, when it is not `vX.Y.Z` or `vX.Y.Z-<prerelease>`, when it differs from the `VERSION` file at the tagged commit, or when the tagged commit is not in `main`. The run's summary gives the reason.
2. `gate` runs the same checks as a pull request (`ci.yml`) on the tagged commit.
3. `publish` runs only if both passed. It builds each image once and pushes it by digest, untagged; creates the exact version tag on both images; moves the floating tags (`1.4`, `latest`); and creates the GitHub release last.

A refused tag has published nothing, but the tag stays: it cannot be deleted or moved (see [Who can release](#who-can-release)), so its version number is spent. Fix what the summary names, bump `VERSION` to the next patch version in a pull request, and tag that.

## A published version never changes

- An exact version tag that exists in GHCR is never overwritten. The workflow does not build that image again, and the tagging script refuses to move such a tag.
- A GitHub release that exists keeps its name, its notes (including edits made by hand), its flags, and every file it has. Only files that are missing are added.
- The git tag itself cannot be moved or deleted: the `release-tags-immutable` ruleset refuses both, for everyone.
- So a mistake in a published version is fixed by releasing the next patch version, never by moving or reusing a tag.

## When a run fails or is interrupted

Nothing needs cleaning up. What a failed run leaves depends on how far it got; the summary of the failed run says which step failed.

| The run failed | What it leaves |
| --- | --- |
| in `plan` or `gate`, or in `publish` before tagging | Nothing anyone can pull. At most untagged image digests in GHCR, which nothing refers to. |
| while tagging | The exact version tags it created stay, possibly on one image only. Floating tags it had moved are put back where they were (the log of the `tag` step says so, and gives the command if one could not be put back). No GitHub release. |
| while creating the GitHub release | Both images under every tag. No release, or a release missing a file. |

**To finish the release, re-run the run**: the "Re-run all jobs" button on the run's page, or `gh run rerun <run id>`. The re-run publishes what is missing and leaves everything else as it is: an image whose exact version exists is not rebuilt, the remaining image tags are completed from the digests already published, an existing release's notes are untouched, and the zip and checksum are attached only if absent. A missing checksum is computed from the zip the release already has. Re-running a release that finished does nothing: it ends green, pushes nothing, and leaves the release unchanged.

Two cases need a decision by hand, and the run says so when it meets them:

- The release has a checksum file but no zip, and the zip built by the re-run does not match it. Delete the checksum (`gh release delete-asset v1.4.2 n8tracks-extension-1.4.2.zip.sha256 --yes`) and re-run.
- An exact version tag points at a digest the run was not given (someone moved it by hand). The run stops before changing anything.

### One release at a time

Releases run one after another; a release that is running is never cancelled by another tag. Of the releases waiting behind it GitHub keeps only the newest: if a third tag is pushed while one release runs and a second waits, the second is cancelled before it started. Nothing of it was published. Re-run it from its page (or `gh run rerun <run id>`) once the others are done.

## Roll back

Going back means running the previous version: every published version stays in GHCR under its exact tag.

1. Set the image to the previous exact version, for example `image: ghcr.io/nathanpond/n8tracks:1.4.1` (and the same tag for `n8tracks-gateway`), then `docker compose pull && docker compose up -d`. Use the exact tag: `latest` and `1.4` keep pointing at the newer version, and nothing moves them back.
2. Install the extension zip from the previous version's GitHub release if the minor version changes; components are compatible when major and minor match.

**The database is not downgraded automatically.** A newer version may have migrated the database, and an older version does not undo that. Going back across a schema change means restoring the backup of the data directory taken before the upgrade; without one, stay on the newer version and wait for a fix. Take a backup of the data directory before every upgrade.

A bad release is withdrawn by releasing a fixed version after it. Its images and its GitHub release stay published.

## First release only

GHCR creates a package as private. If `docker pull ghcr.io/nathanpond/n8tracks:<version>` asks for a login, the owner makes each package public once, in the package's settings on GitHub (Package settings, Danger Zone, Change visibility), for both `n8tracks` and `n8tracks-gateway`. GitHub offers no API for this.
