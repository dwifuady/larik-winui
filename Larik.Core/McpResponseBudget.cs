namespace Larik.Services;

/// <summary>
/// Budget for one MCP tool result. An agent's context is small and a wide table can fill it with a
/// single query, so oversized responses are shortened by dropping rows (the columns, the first rows
/// and the truncation flag survive) instead of being replaced by an apology.
/// </summary>
public static class McpResponseBudget
{
    /// <summary>Serialized payload ceiling, in bytes (1 MiB).</summary>
    public const int MaxBytes = 1_048_576;

    /// <summary>
    /// Drops rows from the largest set first until <paramref name="measure"/> reports a size within
    /// the budget, or nothing is left to drop. Returns how many rows were removed so the caller can
    /// mark the affected result sets as truncated.
    /// </summary>
    public static int Trim(
        IEnumerable<List<string?[]>> sets,
        Func<long> measure,
        int maxBytes = MaxBytes)
    {
        ArgumentNullException.ThrowIfNull(sets);
        ArgumentNullException.ThrowIfNull(measure);

        var rows = sets as IReadOnlyList<List<string?[]>> ?? sets.ToList();
        var dropped = 0;
        var size = measure();

        // Ten percent steps keep the number of re-serializations small on a very wide result.
        for (var attempt = 0; size > maxBytes && attempt < 60; attempt++)
        {
            var largest = rows
                .Where(set => set.Count > 0)
                .OrderByDescending(set => set.Count)
                .FirstOrDefault();

            if (largest is null)
            {
                break;
            }

            var step = Math.Max(1, largest.Count / 10);
            for (var index = 0; index < step && largest.Count > 0; index++)
            {
                largest.RemoveAt(largest.Count - 1);
                dropped++;
            }

            size = measure();
        }

        return dropped;
    }
}
