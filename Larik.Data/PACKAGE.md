# Larik.Data

The provider contract for [Larik SQL Studio](https://github.com/dwifuady/larik-winui) add-ons.

A database provider implements `IDatabaseProvider` and `IDatabaseConnection`; a SQL engine also
implements `ISqlDialect` (or subclasses `SqlLanguage`). The app discovers providers from assemblies,
never from a hard-coded list, so an add-on is a normal assembly that references this package.

## Compatibility

`AssemblyVersion` is pinned at `1.0.0.0`; the semantic contract version is
`LarikDataContract.Version`. Contract changes are additive: a new member needs a default
implementation, records gain nullable `init` properties instead of positional parameters, and
existing members are never renamed or removed. An add-on declares its range in
`larik-addon.json` (for example `">=1.0 <2.0"`).

## What is here

- `IDatabaseProvider`, `IDatabaseConnection`, `ConnectionProfile`, `ProviderSetting`
- `QueryResult`, `TableSchema`, `RoutineSchema`, transaction state
- `IEditorLanguage`, `ISqlDialect`, `SqlLanguage`
- `DatabaseProviderCatalog`, `ProviderSource`, and the add-on loader (`AddonManifest`,
  `AddonLoadContext`, `AddonLocator`)

The full handbook, including packaging and the index format, is in
[`docs/ADDONS.md`](https://github.com/dwifuady/larik-winui/blob/main/docs/ADDONS.md).
