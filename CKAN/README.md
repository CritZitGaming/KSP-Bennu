# CKAN metadata

**Bennu is listed on CKAN.** The submission ([KSP-CKAN/NetKAN#11461](https://github.com/KSP-CKAN/NetKAN/pull/11461))
was merged on 2026-09-05, and the indexer publishes every new GitHub release on its own.

## Where the real metadata lives

The file CKAN actually reads is `NetKAN/Bennu.netkan` in
<https://github.com/KSP-CKAN/NetKAN>. **Editing `Bennu.netkan` in this repo changes nothing
on CKAN** — changes go in as a pull request against that repository, and contributions
there are made under CC-0 (the metadata only, not the mod).

The copy here mirrors the live file so the repo records what CKAN is doing. It differs by
exactly one line, which has not been submitted yet:

```yaml
  - name: ContractConfigurator    # in suggests - the OSIRIS-REx contracts need it
```

The CKAN maintainer rewrote the original submission when merging it. Worth knowing, because
it changes what each release has to get right:

| Field | Reads from | Must stay in sync with |
|---|---|---|
| `$kref` `#/ckan/github/...` | the latest GitHub release and its attached zip | the workflow attaching exactly one zip |
| `x_netkan_version_edit` | the release's git tag, with a leading `v` stripped | `Bennu/Bennu.version` |
| `$vref` `ksp-avc` | `Bennu/Bennu.version` inside the zip | the tag, and where the zip puts the folder |
| *(no `install` stanza)* | CKAN's default: the directory named after the identifier | the pack's folder staying `Bennu` |

The version therefore comes from the **tag**, not the zip filename. The release workflow
already fails the build if the tag and `Bennu.version` disagree, which is the check that
matters.

## Releasing

Push a `v<version>` tag. The `Release` workflow in `.github/workflows/` builds the zip with
`Bennu/` at its root and attaches it, and CKAN picks the release up within a few hours.
Nothing on the CKAN side needs touching.

## Homepage

The maintainer removed `homepage` from the definition, since there is no KSP forum thread
yet. From their merge comment: when there is one, set it as the **website** of the GitHub
repository (the field on the repo's About panel) and CKAN will pick it up from there
automatically. No NetKAN change needed.
