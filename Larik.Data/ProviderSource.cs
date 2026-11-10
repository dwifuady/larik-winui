using System.Reflection;

namespace Larik.Data;

/// <summary>
/// One assembly to scan for <see cref="IDatabaseProvider"/> implementations. The origin is shown in
/// load errors — a built-in name, or <c>larik.mongodb 1.0.0</c> for an add-on — so a failure names
/// the thing the user can remove instead of a bare file path.
/// </summary>
public sealed record ProviderSource(string Origin, Assembly Assembly);
