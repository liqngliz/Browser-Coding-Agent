# Distribution and GitHub Actions plan

## Distribution targets

Ship the same C# application through both distribution channels, with matching release versions:

| Target | Proposed package | Execution | User prerequisites |
| --- | --- | --- | --- |
| .NET tool on NuGet | `BrowserCodingAgent` | `browser-coding-agent` after installation, or `dnx BrowserCodingAgent` | .NET 10 SDK with the required ASP.NET Core runtime, plus installed Chrome/Edge |
| npm package | `@liqngliz/browser-coding-agent` | `npx @liqngliz/browser-coding-agent` | Node >= 20 and npm plus installed Chrome/Edge; launcher bootstraps a private ASP.NET Core runtime |

Both names were unregistered on 2026-10-21 (npm and nuget.org returned 404). Neither distribution is published yet.

## .NET tool / NuGet

Configure the application project for tool packaging:

```xml
<PropertyGroup>
  <IsPackable>true</IsPackable>
  <PackAsTool>true</PackAsTool>
  <PackageId>BrowserCodingAgent</PackageId>
  <ToolCommandName>browser-coding-agent</ToolCommandName>
  <PlaywrightPlatform>all</PlaywrightPlatform>
</PropertyGroup>
```

Create the `.nupkg` using `dotnet pack -c Release -o artifacts/nuget`. Ensure the actual package contains configuration, dependencies, required notices, and the matching `.playwright/` payload. Test installation from the local package feed before publication, including running from a directory outside the repository.

Expected commands after publication:

```sh
dotnet tool install --global BrowserCodingAgent
browser-coding-agent
```

With .NET 10, users can also run it without permanent installation:

```sh
dnx BrowserCodingAgent
```

This distribution runs directly through .NET and needs no application-level Node launcher or runtime bootstrapper. Playwright's bundled Node driver remains an internal dependency. It must share the same configuration behavior, stable browser-profile paths, and response timeout semantics as npm execution.

## npm / npx

Follow the verified `@liqngliz/tokenflow@1.0.2` approach: publish framework-dependent .NET DLLs inside an npm package and use a small Node launcher to bootstrap a private ASP.NET Core 10 runtime on first execution. Users need Node/npm and an installed Chrome or Edge browser, but no .NET SDK or preinstalled .NET runtime. The .NET runtime still requires the host OS's native dependencies.

Implemented: `npm/browser-coding-agent/` (package.json, `browser-coding-agent.cjs` launcher), `scripts/package-npm.mjs`, `scripts/smoke.mjs`. Executable: `browser-coding-agent`.

Expected usage after publication:

```sh
npx @liqngliz/browser-coding-agent
```

Arguments should pass through to ASP.NET Core configuration, for example:

```sh
npx @liqngliz/browser-coding-agent --Browser:Channel=chrome --Chat:ResponseTimeoutMinutes=10
```

## What Tokenflow actually ships

The inspected npm tarball contains `tokenflow.js`, `package.json`, README, and an `app/` directory with compiled .NET assemblies, runtime/dependency manifests, configuration, and dashboard assets. It does not bundle a self-contained .NET runtime.

Its launcher downloads Microsoft's install script and requests `aspnetcore` on channel `10.0`, stores that runtime under `~/.tokenflow/dotnet`, and runs the app DLL with the private `dotnet` executable. Installation happens on first invocation, not through an npm `postinstall` hook. Command-line arguments and terminal input/output pass through to the C# child process.

Its release workflow publishes with `UseAppHost=false`, stages the output into the npm package, copies the README, sets the release version, and publishes the scoped package publicly using npm trusted publishing.

## Files to add when implementing

```text
npm/browser-coding-agent/
  package.json
  browser-coding-agent.cjs
  README.md                     # copied from repository during packaging
  LICENSE                       # copied from repository during packaging
  app/                          # generated dotnet publish output
    BrowserCodingAgent.Api.dll
    BrowserCodingAgent.Api.deps.json
    BrowserCodingAgent.Api.runtimeconfig.json
    Microsoft.Playwright.dll
    appsettings.json
    .playwright/                # driver JavaScript and bundled driver Node binaries
    ...remaining dependencies and license notices
scripts/
  package-npm.mjs
  smoke-npm.mjs
.github/workflows/
  ci.yml
  release.yml
```

Use `package.json` fields `name`, `version`, `bin`, `files`, `engines`, `license`, `repository`, and `publishConfig.access = "public"`. The launcher starts with `#!/usr/bin/env node`; `bin` maps `browser-coding-agent` to it. Explicitly include `app/.playwright` in the file allowlist and verify the actual tarball, rather than assuming a directory beginning with a dot was preserved. Do not publish source build directories, profiles, or local configuration containing secrets.

Publish command once the application exists:

```sh
dotnet publish src/BrowserCodingAgent.Api -c Release \
  --self-contained false -p:UseAppHost=false \
  -p:PlaywrightPlatform=all \
  -o npm/browser-coding-agent/app
```

Keep the regular publish folder layout initially; do not require single-file publishing, trimming, Native AOT, or per-platform self-contained packages for this approach.

## Playwright-specific requirements

Playwright for .NET includes a JavaScript driver plus a Node executable. A normal publish may include only the build/publish platform's driver, so a universal npm package must deliberately include drivers for its supported platforms. The documented `PlaywrightPlatform=all` option supplies a straightforward starting point, at the cost of a larger download. Measure the packed size after implementation.

Preserve the complete `.playwright/` directory, its relative layout, executable permissions, and notices. Test it after npm extraction on every supported platform. Keep the driver files matched to the pinned `Microsoft.Playwright` NuGet version; installing the separate JavaScript `playwright` npm package is not a replacement.

Do not bundle browser binaries or download Chromium at startup. The configured `chrome` or `msedge` channel uses an installed browser; show a clear diagnostic when it is missing. Browser integration tests need a graphical session, or a virtual display on Linux, because this application intentionally opens a visible window.

Implemented: `scripts/package-npm.mjs` deletes `app/.playwright/node/` (563 MB unpacked) and keeps the driver JavaScript (`.playwright/package`, 13 MB, `playwright-core` engines `node >=20`). The launcher sets `PLAYWRIGHT_NODEJS_PATH` to `process.execPath` (honored by Playwright .NET `Driver.cs`) and rejects Node < 20. npm rejected the 213 MB tarball with E413; `package-npm.mjs` now fails above 50 MB packed. The `.nupkg` keeps the bundled Node binaries because .NET tool users may have no Node.

## Launcher responsibilities

1. Validate the supported OS/architecture and locate the packaged DLL by an absolute path relative to the launcher.
2. Bootstrap an app-private **ASP.NET Core** runtime, not just the base .NET runtime. Cache it outside the npm installation, keyed by runtime version and OS/architecture. Pin a tested runtime patch per release and update it regularly; verify `Microsoft.AspNetCore.App` is usable instead of only checking whether `dotnet` exists.
3. Show progress on first invocation. Use a unique temporary installation directory and atomic completion so simultaneous starts or interrupted downloads do not leave a falsely valid runtime. Document installer prerequisites such as PowerShell on Windows or shell/download utilities on Unix.
4. Spawn the private `dotnet` executable with an argument array, the absolute app DLL path, inherited terminal streams, environment configuration, and forwarded CLI arguments. Handle startup errors and preserve exit status. Forward termination and verify API, Playwright driver, and browser processes shut down on Windows and Unix.
5. Separate shipped defaults, user configuration, and writable data. Load shipped defaults from the package, resolve an explicit external config path against the caller's directory, and preserve environment/CLI overrides. Avoid silently treating npm's cache directory as a user's configuration directory.
6. Store persistent browser profiles under a stable per-user app directory, such as `~/.browser-coding-agent/profiles/msedge` or `.../chrome`, unless explicitly overridden. A runtime cache and browser profiles are different directories with different lifetimes. Never store profiles in `node_modules`, the npx cache, or the package's `app/` directory.

These changes do not add timing configuration. `Chat.ResponseTimeoutMinutes` remains the only user-configurable timing option for Playwright-to-M365 interaction; first-run installation and manual sign-in are outside that budget.

## Release and validation requirements

- Build and test the C# application, publish it into staging, and assemble the npm package with a version matching the release tag. Copy README, LICENSE, and required dependency notices.
- Inspect `npm pack --dry-run --json`, then create the real `.tgz`. Assert that the DLL, runtime manifests, configuration, and complete driver are present and that profiles are absent.
- Run the actual tarball using `npx --yes --package=/absolute/path/package.tgz -- browser-coding-agent` from a neutral directory. Exercise an empty runtime cache on a machine without .NET, a cached second start, paths containing spaces, configuration overrides, and Ctrl+C cleanup.
- Verify browser startup, health endpoints, and a local simulated chat round trip. Test both Chrome and Edge where available. Keep real M365 sign-in tests manual/opt-in.
- Begin with Windows x64 and macOS x64/Arm64 as proposed test targets. Add Linux only after testing its desktop and native runtime/browser prerequisites. Reject untested combinations clearly rather than inferring support from portable DLLs alone.
- Arrange publish access to the npm scope and the initial package publication, then configure GitHub Actions as an npm trusted publisher for this repository and workflow. Use `id-token: write` and a supported Node/npm pair; current npm documentation requires Node >=22.14.0 and npm >=11.5.1 for trusted publishing. These are publishing prerequisites, distinct from the launcher runtime minimum, which should be set from actual tests.

Measured 2026-10-21 (macOS arm64): `.nupkg` 213 MB (nuget.org limit 250 MB); npm `.tgz` 4.4 MB packed, 15.6 MB unpacked (without bundled Node). Both passed `scripts/smoke.mjs` locally (startup, health, simulated chat round trip, streamed later turn, SIGINT shutdown) and in CI on Windows, macOS arm64, and macOS x64.

## GitHub Actions deliverables

Implemented as `.github/workflows/build.yml` (reusable: test, pack, smoke), `ci.yml`, and `release.yml`. All actions pinned to commit SHAs.

### `ci.yml` — pull requests and pushes to main

1. Set up the pinned .NET SDK and supported Node/npm tooling.
2. Restore, build, and run the C# tests.
3. Build both the `.nupkg` tool and npm `.tgz`, including Playwright drivers and license notices. Inspect their contents.
4. Test tool installation from the local NuGet feed using an isolated tool path and test npm execution from the actual tarball in a neutral directory. Exercise configuration overrides, startup/health, simulated chat, and process cleanup.
5. Run the supported Windows/macOS matrix and a separate clean environment without .NET for the npm bootstrap check. Test first-run installation and cached startup. Use installed browser channels for browser checks; real M365 account access remains manual/opt-in.
6. Upload package artifacts and relevant test logs for review. CI does not publish packages.

### `release.yml` — version tags such as `v0.1.0`

1. Check out the tagged commit and validate its semantic version.
2. Run the same build and package checks against that commit. Apply the tag version to the C# assembly, NuGet package, and npm package.
3. Build each package once, test those exact artifacts, and upload them with checksums. Publish jobs consume those tested artifacts rather than rebuilding.
4. Publish the `.nupkg` to NuGet and `.tgz` to npm, with each publish job depending on all required verification jobs. Stable tags go to npm `latest`; prerelease tags use a prerelease dist-tag.
5. Use separate NuGet and npm trusted-publisher configurations scoped to this repository and `release.yml`. NuGet OIDC authentication can obtain a short-lived publish key; npm supports direct trusted publishing. Grant `id-token: write` only to the jobs that need it and retain checkout's `contents: read` permission.
6. Plan initial account/package setup for each registry. A working publisher configuration on one registry does not authorize publication on the other. Handle partial releases by verifying and skipping an already-published identical version before retrying the missing publication; never overwrite or silently replace immutable package versions.

Release acceptance requires both packages to pass verification and be available at the matching version. Creating the workflows does not itself create registry accounts or configure external publisher trust.

## Sources inspected

- [Tokenflow npm metadata](https://registry.npmjs.org/@liqngliz%2ftokenflow/latest) and [version 1.0.2 tarball](https://registry.npmjs.org/@liqngliz/tokenflow/-/tokenflow-1.0.2.tgz)
- [Tokenflow launcher](https://github.com/liqngliz/ConduitSharp/blob/main/npm/tokenflow/tokenflow.js), [release workflow](https://github.com/liqngliz/ConduitSharp/blob/main/.github/workflows/tokenflow.yml), and [package smoke test](https://github.com/liqngliz/ConduitSharp/blob/main/npm/tokenflow/smoke.sh)
- [.NET publishing modes](https://learn.microsoft.com/en-us/dotnet/core/deploying/) and [runtime install scripts](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script)
- [Playwright .NET driver bundling](https://playwright.dev/dotnet/docs/library#bundle-drivers-for-different-platforms) and [installed browser channels](https://playwright.dev/dotnet/docs/browsers)
- [Playwright driver lookup source](https://github.com/microsoft/playwright-dotnet/blob/main/src/Playwright/Helpers/Driver.cs) and [publish build targets](https://github.com/microsoft/playwright-dotnet/blob/main/src/Playwright/build/Microsoft.Playwright.targets)
- [npm package.json fields](https://docs.npmjs.com/cli/v11/configuring-npm/package-json/) and [trusted publishing](https://docs.npmjs.com/trusted-publishers/)
- [.NET tool packaging](https://learn.microsoft.com/en-us/dotnet/core/tools/global-tools-how-to-create) and [one-shot execution / dnx](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-tool-exec)
