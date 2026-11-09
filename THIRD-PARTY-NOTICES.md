# Third-party notices

Larik is MIT licensed; see `LICENSE`. This build ships the
following third-party components. Versions are the ones restored by
`Larik/Larik.csproj` at release time.

## Runtime and framework

| Component | Version | License |
| --- | --- | --- |
| .NET 10 / .NET Desktop Runtime | 10.0.x | MIT |
| Microsoft Windows App SDK (WinUI 3, MSIX, AI/ML, Widgets) | 2.6.5 | Microsoft Software License Terms (redistributable with the app) |
| Microsoft Edge WebView2 SDK | 1.0.3719.77 | BSD-style Microsoft license |

The portable ZIP is self-contained: it includes the .NET and Windows App SDK
runtime files listed above. The installed build includes the same files.

## Libraries

| Component | Version | License |
| --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| Microsoft.Data.Sqlite (and Microsoft.Data.Sqlite.Core) | 10.0.14 | MIT |
| SQLitePCLRaw (core, bundle_e_sqlite3, lib.e_sqlite3, provider.e_sqlite3) | 2.1.13 | Apache-2.0 |
| SQLite core library | 3.x (bundled by SQLitePCLRaw) | Public domain |
| Microsoft.Data.SqlClient (and SNI runtime) | 7.1.1 / 7.1.0 | MIT |
| Npgsql | 10.0.3 | PostgreSQL License (BSD-style) |
| Microsoft.IdentityModel.* and System.IdentityModel.Tokens.Jwt | 8.16.0 | MIT |
| Microsoft.Extensions.* (caching, options, logging, DI abstractions) | 9.0.18 / 10.0.0 | MIT |
| System.Security.Cryptography.Pkcs | 10.0.14 | MIT |
| System.Security.Cryptography.ProtectedData | 9.0.18 | MIT |
| System.Configuration.ConfigurationManager | 9.0.18 | MIT |
| System.Diagnostics.EventLog | 9.0.18 | MIT |
| System.Threading.RateLimiting | 9.0.18 | MIT |
| Microsoft.Bcl.Cryptography | 9.0.18 | MIT |

## Build-only packages

`Microsoft.Windows.SDK.BuildTools` and `Microsoft.Windows.SDK.BuildTools.MSIX`
are used to compile and package the app; they are not redistributed as app
components. `Microsoft.Windows.SDK.BuildTools.WinApp` provides local `dotnet
run` support in development.

## Notes

- No telemetry, analytics, or crash-reporting SDK is included.
- The app connects only to the database servers you configure. Passwords are
  stored in Windows Credential Manager, not in the SQLite store.
- Full license texts are available from each package's NuGet page and from the
  .NET, Windows App SDK, and WebView2 repositories.
