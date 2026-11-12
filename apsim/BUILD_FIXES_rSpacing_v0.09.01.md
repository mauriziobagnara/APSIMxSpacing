# Build fixes after the first Windows compilation

## APSIM.Shared / Mapsui

`MapRenderer.cs` was migrated from removed Mapsui APIs to the current renderer contract:

- rendering uses the `Map`-owned `Navigator`;
- `RenderToBitmapStream(Map, float, RenderFormat)` replaces the obsolete viewport/layer overload;
- asynchronous calls to removed `RefreshData`, `AbortFetch`, and `Cancelled` members were removed;
- bitmap-registry markers were replaced by a vector `SymbolStyle`;
- obsolete `FetchingPostponedInMilliseconds` assignment was removed.

No file was excluded from compilation and no compiler diagnostic was suppressed.

## Models / SQL Server

`WriteTableToSqlServer.cs` now imports `Microsoft.Data.SqlClient`. `Models.csproj` now references `Microsoft.Data.SqlClient` 6.1.3 instead of the obsolete `System.Data.SqlClient` 4.8.6 package. No warning was downgraded or suppressed.
