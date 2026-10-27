# Releasing FxAuto

Releases are built and published by GitHub Actions (`.github/workflows/release.yml`) when a
version tag is pushed. Nothing is uploaded by hand.

## 1. Bump the version

The product version lives in `src/FxAuto/FxAuto.csproj`. If the project has no explicit
`<Version>`, the SDK default `1.0.0` applies; add one to move past it:

```xml
<PropertyGroup>
  <Version>1.1.0</Version>
</PropertyGroup>
```

Commit the bump on the release branch (normally `main`) and make sure CI is green.

## 2. Tag and push

```sh
git tag v1.1.0 && git push origin v1.1.0
```

- The tag must be `vX.Y.Z`, optionally with a pre-release suffix (`v1.1.0-beta.1`).
- The tag must match the project `<Version>`: `v1.1.0` needs `1.1.0`; `v1.1.0-beta.1` is
  accepted with `1.1.0-beta.1` or `1.1.0`. Otherwise the workflow stops with a clear error and
  nothing is published (delete the tag, fix the version, tag again).

## 3. What the workflow does

1. Checks out the tag and sets up .NET 10 (with a NuGet cache).
2. Verifies the tag against the project version.
3. Builds `FxAuto.sln` in Release with warnings as errors.
4. Runs the test console (`dotnet run --project tests/FxAuto.Tests -c Release`); any failure stops the release.
5. Ensures Inno Setup 6 is available (installs it with Chocolatey if the runner lacks it).
6. Runs `installer/build.ps1 -Version vX.Y.Z`, which publishes FxAuto self-contained and
   produces `installer/Output/FxAuto-Setup-X.Y.Z.exe`.
7. Writes `FxAuto-Setup-X.Y.Z.exe.sha256` in `sha256sum` format
   (`<lowercase hex>  FxAuto-Setup-X.Y.Z.exe`).
8. Creates the GitHub Release with `gh release create --generate-notes`, attaching both files.
   Tags containing `-` are marked as pre-release; the others are marked as latest.

The in-app updater expects exactly those two assets. By default it reads `releases/latest`, so
pre-releases are only offered to users who turn on **Include pre-releases** in Settings → General → Updates
(it then reads `releases` and picks the newest version, pre-release or not). The installer file name always uses the `X.Y.Z` core
(a `v1.1.0-beta.1` tag produces `FxAuto-Setup-1.1.0.exe`).

## Security notes

- Workflows default to `contents: read`; only the release job gets `contents: write`.
- Third-party actions are pinned by commit SHA (version in a comment). Update them by resolving
  the new tag's SHA, e.g. `gh api repos/actions/checkout/git/ref/tags/<tag>`.
- The only secret used is `GITHUB_TOKEN`.
- Code signing is prepared but disabled: to enable it, add the `SIGN_CERT_BASE64` (base64 of the
  .pfx) and `SIGN_CERT_PASSWORD` repository secrets and uncomment the job `env` block and the
  "Sign installer" step in `release.yml`. It signs the installer before the checksum is computed.

## If something fails

- Wrong version: delete the tag (`git push origin :refs/tags/v1.1.0` and `git tag -d v1.1.0`),
  fix `<Version>`, tag again.
- Failure after the release was created: delete the release and the tag on GitHub, then re-tag.
