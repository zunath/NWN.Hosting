# Releasing the Nwn.Hosting packages

Three packages are released together: `Nwn.Hosting.Contracts`, `Nwn.Hosting` and
`Nwn.Hosting.Testing`. They share one version and one MIT license.

## Versioning

`NwnHostingVersion` in `Directory.Build.props` is the single version for the family. The
first public release is `0.1.0-preview.1`. Override it for a build with
`-p:NwnHostingVersion=X` or `-p:Version=X`; the release script takes `-Version X`.

Packages depend on each other through project references, so a pack of one build always
produces dependencies on exactly the same version. Bump `NwnHostingVersion` for every
release; nuget.org versions are immutable.

## Building the packages

```powershell
powershell -ExecutionPolicy Bypass -File tools/Pack-Packages.ps1
```

The script restores, builds Release, runs the test project (any failure stops it),
then packs all three packages and their `.snupkg` symbol packages into
`artifacts/release/<version>`. It refuses to run if any target package file already exists
and never pushes. It prints each file's SHA-256 and the push commands. Nothing here needs game
data or environment variables.

Use `-Version` and `-OutputDirectory` to change either default.

Packages embed the current git commit hash, so run the script from a clean, committed tree.

Before pushing, inspect the `.nupkg` files (they are zip files): the nuspec should list
the license expression, readme, description, tags and dependencies at the same version, and
the package should contain `README.md` and `licenses/NWN-Hosting-MIT.txt`.

## Publishing to nuget.org

1. Sign in to nuget.org as the owner (`zunath`) and create an API key under
   *API Keys*. Scope it to *Push new packages and package versions* and set the glob
   pattern to `Nwn.Hosting*`. Set a short expiry.
2. Put the key in your shell only; never commit it or paste it into a script:

   ```powershell
   $env:NUGET_API_KEY = '<your key>'
   ```
3. Run the push commands the script printed, dependencies first:

   ```powershell
   dotnet nuget push artifacts\release\<version>\Nwn.Hosting.Contracts.<version>.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
   dotnet nuget push artifacts\release\<version>\Nwn.Hosting.<version>.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
   dotnet nuget push artifacts\release\<version>\Nwn.Hosting.Testing.<version>.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_API_KEY
   ```

   The matching `.snupkg` in the same directory is pushed to the symbol server by the same
   command. If it is not, push it explicitly the same way.
4. Remove the key from the environment (`Remove-Item Env:NUGET_API_KEY`). New packages
   take a few minutes to validate and index before they appear.

## Switching consumers from a local feed

nuget.org signs every package it accepts, so the bytes on nuget.org differ from the local
`.nupkg` files and the content hashes change. Consumers that restored from a local folder
feed and have `packages.lock.json` files must regenerate them after switching to the public
packages (`dotnet restore --force-evaluate`) and review the resulting lock-file diff.

## Repository URL

Package metadata points at https://github.com/zunath/NWN.Hosting through
`NwnHostingRepositoryUrl` in `Directory.Build.props`, which fills `RepositoryUrl`,
`RepositoryType` and `PackageProjectUrl` and enables `PublishRepositoryUrl`. A fork sets its
own URL there or passes `-p:NwnHostingRepositoryUrl=...`. The .NET SDK's built-in Source
Link maps sources to the `origin` remote, so pack from a clone whose `origin` is the public
repository. `EmbedUntrackedSources` and symbol packages are enabled.
