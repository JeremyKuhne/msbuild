# Localizing MSBuild

## Localizable resource structure

- Neutral resources: [*resx](https://github.com/search?utf8=%E2%9C%93&q=repo%3AMicrosoft%2Fmsbuild+extension%3Aresx&type=Code&ref=advsearch&l=&l=)
- shared resources live in `Microsoft.Build.Framework`'s `SR.resx` and are consumed by the other MSBuild assemblies through `SR.ResourceManager` (exposed via `AssemblyResources.SharedResources`)
- internal accessors are emitted by the build-only [resource generator](../../src/ResourceGenerator/ResourceGenerator.csproj). Primary catalogs use `PrimaryResourceCatalog`; Framework retains its existing `SR` shape. The generator does not change the binary resource payload or the translation workflow.
- each neutral resource has a directory named `xlf` besides it which contains its localized strings in .xlf format
- there is one language per xlf
- the logical name for a resource is: `<Assembly Name>.<Neutral Resx File Name>.resources`. In the ResourceManager this appears as `<Assembly Name>.<Neutral Resx File Name>` (without the trailing `.resources`). For example, the `Microsoft.Build` assembly uses the `Microsoft.Build.Strings.resources` [logical resource name](https://github.com/dotnet/msbuild/blob/cc3db358d34ad4cd1ec0c67e17582d7ca2a15040/src/Build/Microsoft.Build.csproj#L792) (the resource file is `Strings.resx`), and its corresponding [ResourceManager](https://github.com/dotnet/msbuild/blob/518c041f4511a6bc23eb40703b69a94ea46c65fd/src/Build/Resources/AssemblyResources.cs#L118) uses `Microsoft.Build.Strings`.

## How to edit a resource

- if you need to add / remove / update a resource, only do so in the neutral resx files. xlf files get automatically updated during localized builds.

## What a localized build does

- converts xlf files to localized resx files
- the localized resx files are generated into the `$(IntermediaryOutputPath)`
- produces satellite assemblies for each language
- the core assemblies declare `NeutralResourcesLanguage("en")` with main-assembly fallback. Requests for that neutral language use the main-assembly catalog; other cultures probe satellites and parents before neutral fallback.

## Indexed string-resource experiment

`MSBUILD_USE_INDEXED_STRING_RESOURCES=1` selects the Touki-derived internal
string-only lookup implementation before a catalog's first use. It is disabled
by default pending compatibility and performance validation. This is an
experimental implementation selector, not a language or deployment setting.

Both neutral and localized catalogs participate. Exact keys and text, explicit
cultures, and primary-before-shared fallback remain unchanged. Framework's
`SR.Culture` affects direct accessors, not a wrapper supplying its own culture.
Generated cache slots are culture-specific and reset with `ReleaseAllResources`.
Case-insensitive and non-string/resource-set APIs retain platform behavior;
NativeAOT and neutral-in-satellite deployments use the platform resource path.

Built-in callers use generated string properties or the nested `Resources`
properties. The latter return internal lazy references carrying the exact key
and constant slot, so logging/validation helpers can preserve filtering,
deferred formatting, and key-based help keywords without dynamic key dispatch.
They do not replace resource text with generated literals. Custom task resource
managers and derived loggers retain their existing public resource hooks.

See [string-resource measurement](../string-resource-performance.md) for source
provenance, deviations, benchmark commands, and the cold-build protocol.

## Process for interacting with the localization team

- 3 weeks cadence for main, initiated by loc team
- on demand for main / release branches, initiated by msbuild team

## Contributing a better translation

- send a PR with an updated `<target>` element of the xlf resource (do not include other non-localization changes)
- we will notify the localization team, which will then take over and review the PR

## Localizing XSD "IntelliSense"

Code completion ("IntelliSense") for MSBuild project files is provided minimally in Visual Studio by XML Schema files like [`Microsoft.Build.CommonTypes.xsd`](https://github.com/dotnet/msbuild/blob/ba9a1d64a7abf15a8505827c00413156a3eb7f62/src/MSBuild/MSBuild/Microsoft.Build.CommonTypes.xsd). These files are English-only in the GitHub repo; their localization is managed in the Microsoft-internal `VS` repo.

### If there is a bug in XSD localization

File XSD localization bugs in this repo. The MSBuild team will coordinate with the Visual Studio localization team to redirect it appropriately.

### When an XSD has been updated

After updating an XSD in the GitHub repo, the MSBuild-to-VS-repo insertion process automatically updates the canonical Visual Studio copy of the XSD.
