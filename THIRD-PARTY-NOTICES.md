# Third-party notices

Adelaide does not vendor third-party source code. It references the packages below, which are restored from NuGet and
npm at build time and remain under their own licenses. License identifiers are taken from each package's own metadata.

## .NET (direct references)

| Package | License |
|---|---|
| Azure.Core, Azure.Identity, Azure.Security.KeyVault.Secrets, Azure.Storage.Files.Shares | MIT |
| Microsoft.AspNetCore.Authentication.JwtBearer, Microsoft.Extensions.* (DependencyInjection, Hosting, Http.Resilience, Logging) | MIT |
| Microsoft.Data.SqlClient, Microsoft.Data.Sqlite | MIT |
| Microsoft.CodeAnalysis.BannedApiAnalyzers | MIT |
| System.Security.Cryptography.Pkcs | MIT |
| PdfSharp | MIT |
| PDFtoImage (bundles PDFium via bblanchon.PDFium, Apache-2.0; and SkiaSharp, MIT) | MIT |
| ZXingCpp (zxing-cpp .NET wrapper) | Apache-2.0 |
| SQLitePCLRaw.lib.e_sqlite3 (SQLite is public domain) | Apache-2.0 |
| Serilog.AspNetCore, Serilog.Sinks.File, Serilog.Sinks.ApplicationInsights | Apache-2.0 |
| nClam | Apache-2.0 |
| xunit, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk, coverlet.collector | MIT |

## Angular client (npm)

Runtime dependencies: `@angular/*` (MIT), `rxjs` (Apache-2.0), `tslib` (0BSD) and their MIT/BSD transitive packages.
The development toolchain (Angular CLI and build, TypeScript, Vitest, jsdom, Prettier) is MIT, ISC, BSD, Apache-2.0
or BlueOak-1.0.0. No copyleft licenses appear in the dependency tree.
