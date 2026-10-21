# Publishing

| Registry | Package | Install / run |
| --- | --- | --- |
| nuget.org | `BrowserCodingAgent` (.NET tool) | `dotnet tool install -g BrowserCodingAgent` / `dnx BrowserCodingAgent` |
| npm | `@liqngliz/browser-coding-agent` | `npx @liqngliz/browser-coding-agent` |

Workflow: `.github/workflows/release.yml`, triggered by a `v<semver>` tag. Auth: OIDC trusted publishing on both registries; no API keys stored.

## One-time setup

### GitHub

- Settings → Environments → create `release`. Optional: required reviewers (manual approval per publish), deployment rule restricted to tags `v*`.
- Secret `NUGET_USER` = nuget.org profile name, not email. Repository secret or `release` environment secret.

### nuget.org

Username → Trusted Publishing → Add policy:

| Field | Value |
| --- | --- |
| Policy owner | account that will own `BrowserCodingAgent` |
| Repository Owner | `liqngliz` |
| Repository | `Browser-Coding-Agent` |
| Workflow File | `release.yml` |
| Environment | `release` |
| Scope | must allow pushing new packages (first release creates the ID) |

No manual first publish needed. A policy nuget.org cannot yet bind to GitHub repo/owner IDs is temporarily active for 7 days; the first successful publish makes it permanent. The window can be restarted from the UI.

### npm

A trusted publisher can only be configured on an existing package, and a new configuration expires if no publish succeeds through it within 2 days. Bootstrap:

1. Let a `ci` run on `main` pass.
2. Download its `packages` artifact (Actions → run → Artifacts).
3. Publish the tested tarball once from a logged-in machine:
   ```sh
   npm publish liqngliz-browser-coding-agent-0.0.0-ci.<N>.tgz --access public --tag bootstrap
   ```
4. npmjs.com → `@liqngliz/browser-coding-agent` → Settings → Trusted publishing → GitHub Actions:

   | Field | Value |
   | --- | --- |
   | Organization or user | `liqngliz` |
   | Repository | `Browser-Coding-Agent` |
   | Workflow filename | `release.yml` |
   | Environment | `release` |

   Case-sensitive. A mismatch fails as a 404 on publish, not as a configuration error.
5. Push the first release tag within 2 days.

Provenance is attached automatically (public repo + trusted publishing). It requires `repository.url` in `npm/browser-coding-agent/package.json` to match this repo.

## Release

1. `main` green in `ci`.
2. If needed, bump `RUNTIME_VERSION` in `npm/browser-coding-agent/browser-coding-agent.cjs` (pinned ASP.NET Core patch the npm launcher installs).
3. Tag and push:
   ```sh
   git tag v0.1.0
   git push origin v0.1.0
   ```
4. Approve the `release` environment if reviewers are configured (once per publish job).
5. Verify:
   ```sh
   npm view @liqngliz/browser-coding-agent dist-tags
   dotnet package search BrowserCodingAgent --exact-match
   dnx BrowserCodingAgent@<version>
   ```
   nuget.org indexing can lag the push by several minutes.

Version comes from the tag; `<Version>` in the csproj applies to local builds only.

| Tag | Version | npm dist-tag | NuGet |
| --- | --- | --- | --- |
| `v1.2.3` | `1.2.3` | `latest` | stable |
| `v1.2.3-rc.1` | `1.2.3-rc.1` | `next` | prerelease |
| anything else | rejected by the `version` job | | |

## release.yml jobs

| Job | Does | Needs |
| --- | --- | --- |
| `version` | validates tag, derives version and npm dist-tag | |
| `build` | `build.yml`: tests (ubuntu/windows/macos), packs `.nupkg` + `.tgz` once, content and size checks, smoke tests on windows, macOS arm64, macOS x64, npm bootstrap in a container without .NET; uploads `packages` (30-day retention) | `version` |
| `publish-nuget` | verifies `SHA256SUMS`, `NuGet/login` (1-hour key), `dotnet nuget push --skip-duplicate` | `version`, `build` |
| `publish-npm` | verifies `SHA256SUMS`, npm 11, `npm publish <tgz>` unless already published | `version`, `build` |

Publish jobs consume the artifacts `build` tested; nothing is rebuilt. Only publish jobs get `id-token: write`.

## Post-release smoke

`post-release-smoke.yml` runs after every `release` run (also when npm failed) and on demand:

| Job | Does |
| --- | --- |
| `version` | tag `v<semver>` of the release run, or the `version` input |
| `wait-for-nuget` | polls `api.nuget.org/v3-flatcontainer/browsercodingagent/index.json` every 30s, up to 90 min (validation + indexing) |
| `smoke` | windows, macOS arm64, macOS x64: `dotnet tool install` from nuget.org, installed version check, `scripts/smoke.mjs --channel msedge` (installed Edge, headless) |

Manual run: Actions → post-release-smoke → Run workflow → version (e.g. `0.1.1`). Logs: `post-release-smoke-logs-<os>` artifact on failure.

## Partial failure and retry

- Actions → failed run → Re-run failed jobs. Reuses the run's `packages` artifact.
- NuGet: an existing version is skipped (`--skip-duplicate`).
- npm: an existing version with identical `dist.integrity` is skipped; different contents fail the job.
- Both registries treat versions as immutable. To change shipped bits, tag a new version.

## Troubleshooting

| Symptom | Check |
| --- | --- |
| `npm publish` 404 | trusted publisher fields or environment mismatch; configuration expired (2 days without a publish) |
| `NuGet/login` fails | policy owner/repo/workflow/environment mismatch; `NUGET_USER` set to email instead of profile name; policy inactive after 7-day window |
| pack job: exceeds 250 MB | nuget.org size limit; Playwright upgrade added driver weight |
| smoke job fails | `smoke-logs-<os>` artifact holds the app log |
| test job fails | `test-results-<os>` artifact holds `.trx` files |
| `already published with different contents` | version already on npm from another build; tag a new version |

## Local dry run

```sh
dotnet test --filter "Category!=ManualLiveValidation"
dotnet pack src/BrowserCodingAgent.Api -c Release -o artifacts/nuget -p:Version=0.0.0-local.1
node scripts/package-npm.mjs --version 0.0.0-local.1
dotnet tool install BrowserCodingAgent --tool-path /tmp/bca-tool --add-source artifacts/nuget --version 0.0.0-local.1
node scripts/smoke.mjs --tool /tmp/bca-tool/browser-coding-agent
node scripts/smoke.mjs --npm artifacts/npm/liqngliz-browser-coding-agent-0.0.0-local.1.tgz
```

First npm smoke installs the runtime into `~/.browser-coding-agent/dotnet/<version>-<platform>-<arch>`.

## Withdrawing a version

- npm: `npm deprecate @liqngliz/browser-coding-agent@<version> "<reason>"`; `npm unpublish` only within npm's unpublish policy window.
- nuget.org: unlist via the package's Manage page; versions cannot be deleted.
