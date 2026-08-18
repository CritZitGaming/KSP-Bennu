# CKAN metadata

`Bennu.netkan` is this mod's CKAN indexing metadata. It lives here so it is version
controlled alongside the pack, but **CKAN does not read it from this repo** — it has to be
submitted to the CKAN metadata index.

## Getting listed

1. Publish a GitHub release with an attached zip named `Bennu-<version>.zip` containing
   `GameData/Bennu/` at the zip root. The `Release` workflow in `.github/workflows/` does
   this automatically when you push a `v<version>` tag.
2. Fork <https://github.com/KSP-CKAN/NetKAN>.
3. Copy `Bennu.netkan` into that fork's `NetKAN/` directory.
4. Open a pull request. The CKAN bot validates and inflates the file on the PR; if it
   builds cleanly a maintainer merges it, and the indexer picks up every future release
   automatically.

## How the pieces connect

| Field | Reads from | Must stay in sync with |
|---|---|---|
| `$kref` … `version_from_asset` | the release asset filename | the zip name the workflow builds |
| `$vref` `ksp-avc` | `GameData/Bennu/Bennu.version` inside the zip | the git tag, minus its leading `v` |
| `install: find: Bennu` | the `GameData/Bennu` directory in the zip | the pack's folder name |

The version number therefore appears in three places for every release — the git tag, the
zip filename, and `Bennu.version`. The release workflow fails the build if the tag and
`Bennu.version` disagree.

## After the first release

Nothing further is needed. Once the netkan is merged, tagging a new release is enough —
the CKAN indexer polls GitHub and publishes the new version on its own.
