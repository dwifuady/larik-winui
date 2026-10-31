using Larik.Models;

namespace Larik.Services;

/// <summary>
/// Built-in snippets, ported 1:1 from the desktop app's Rust catalog
/// (src-tauri/src/storage/snippets.rs) so muscle memory carries over.
/// ${cursor} marks the final caret; ${1:placeholder} marks a selectable placeholder.
/// </summary>
public static class SnippetCatalog
{
    private static readonly string[] MssqlOnly = ["mssql"];
    private static readonly string[] PostgresOnly = ["postgres"];

    public static IReadOnlyList<SnippetDef> All { get; } =
    [
        new() { Trigger = "sel", Name = "SELECT *", Category = "Select", Description = "Select all columns from a table", Content = "SELECT * FROM ${cursor}" },
        new() { Trigger = "selt", Name = "SELECT TOP", Category = "Select", Description = "Select top N rows", Content = "SELECT TOP ${1:100} * FROM ${cursor}", Drivers = MssqlOnly },
        new() { Trigger = "sell", Name = "SELECT LIMIT", Category = "Select", Description = "Select with LIMIT", Content = "SELECT * FROM ${1:table} LIMIT ${2:100}${cursor}", Drivers = PostgresOnly },
        new() { Trigger = "selc", Name = "SELECT COUNT", Category = "Select", Description = "Count rows in a table", Content = "SELECT COUNT(*) FROM ${cursor}" },
        new() { Trigger = "seld", Name = "SELECT DISTINCT", Category = "Select", Description = "Select distinct values", Content = "SELECT DISTINCT ${1:column} FROM ${cursor}" },
        new() { Trigger = "selw", Name = "SELECT WHERE", Category = "Select", Description = "Select with WHERE clause", Content = "SELECT * FROM ${1:table} WHERE ${cursor}" },
        new() { Trigger = "selj", Name = "SELECT JOIN", Category = "Select", Description = "Select with INNER JOIN", Content = "SELECT ${1:columns}\nFROM ${2:table1} t1\nINNER JOIN ${3:table2} t2 ON t1.${4:id} = t2.${5:id}\nWHERE ${cursor}" },
        new() { Trigger = "sellj", Name = "SELECT LEFT JOIN", Category = "Select", Description = "Select with LEFT JOIN", Content = "SELECT ${1:columns}\nFROM ${2:table1} t1\nLEFT JOIN ${3:table2} t2 ON t1.${4:id} = t2.${5:id}\nWHERE ${cursor}" },
        new() { Trigger = "selg", Name = "SELECT GROUP BY", Category = "Select", Description = "Select with GROUP BY", Content = "SELECT ${1:column}, COUNT(*) AS Count\nFROM ${2:table}\nGROUP BY ${1:column}\nORDER BY Count DESC" },

        new() { Trigger = "ins", Name = "INSERT INTO", Category = "Insert", Description = "Insert a single row", Content = "INSERT INTO ${1:table} (${2:columns})\nVALUES (${cursor})" },
        new() { Trigger = "inss", Name = "INSERT SELECT", Category = "Insert", Description = "Insert from a SELECT", Content = "INSERT INTO ${1:target_table} (${2:columns})\nSELECT ${3:columns}\nFROM ${cursor}" },

        new() { Trigger = "upd", Name = "UPDATE", Category = "Update", Description = "Update rows", Content = "UPDATE ${1:table}\nSET ${2:column} = ${3:value}\nWHERE ${cursor}" },
        new() { Trigger = "updj", Name = "UPDATE JOIN", Category = "Update", Description = "Update with JOIN", Content = "UPDATE t1\nSET t1.${1:column} = t2.${2:column}\nFROM ${3:table1} t1\nINNER JOIN ${4:table2} t2 ON t1.${5:id} = t2.${6:id}\nWHERE ${cursor}", Drivers = MssqlOnly },

        new() { Trigger = "del", Name = "DELETE", Category = "Delete", Description = "Delete rows", Content = "DELETE FROM ${1:table}\nWHERE ${cursor}" },
        new() { Trigger = "trunc", Name = "TRUNCATE", Category = "Delete", Description = "Truncate a table", Content = "TRUNCATE TABLE ${cursor}" },

        new() { Trigger = "ct", Name = "CREATE TABLE", Category = "DDL", Description = "Create a new table", Content = "CREATE TABLE ${1:table_name} (\n    ${2:id} INT IDENTITY(1,1) PRIMARY KEY,\n    ${cursor}\n)", Drivers = MssqlOnly },
        new() { Trigger = "ata", Name = "ALTER TABLE ADD", Category = "DDL", Description = "Add a column", Content = "ALTER TABLE ${1:table}\nADD ${2:column} ${cursor}" },
        new() { Trigger = "atd", Name = "ALTER TABLE DROP", Category = "DDL", Description = "Drop a column", Content = "ALTER TABLE ${1:table}\nDROP COLUMN ${cursor}" },
        new() { Trigger = "ci", Name = "CREATE INDEX", Category = "DDL", Description = "Create an index", Content = "CREATE INDEX IX_${1:table}_${2:column}\nON ${1:table} (${2:column})" },
        new() { Trigger = "di", Name = "DROP INDEX", Category = "DDL", Description = "Drop an index", Content = "DROP INDEX ${1:index_name} ON ${cursor}", Drivers = MssqlOnly },

        new() { Trigger = "cte", Name = "CTE", Category = "CTE", Description = "Common table expression", Content = ";WITH ${1:cte_name} AS (\n    ${2:SELECT * FROM table}\n)\nSELECT * FROM ${1:cte_name}${cursor}" },
        new() { Trigger = "rcte", Name = "Recursive CTE", Category = "CTE", Description = "Recursive CTE", Content = ";WITH ${1:cte_name} AS (\n    -- Anchor\n    SELECT ${2:columns}\n    FROM ${3:table}\n    WHERE ${4:condition}\n\n    UNION ALL\n\n    -- Recursive\n    SELECT ${2:columns}\n    FROM ${3:table} t\n    INNER JOIN ${1:cte_name} c ON t.${5:parent_id} = c.${6:id}\n)\nSELECT * FROM ${1:cte_name}${cursor}", Drivers = MssqlOnly },

        new() { Trigger = "cp", Name = "CREATE PROCEDURE", Category = "Procedure", Description = "Create a stored procedure", Content = "CREATE PROCEDURE ${1:procedure_name}\n    @${2:param} ${3:INT}\nAS\nBEGIN\n    SET NOCOUNT ON;\n    ${cursor}\nEND", Drivers = MssqlOnly },
        new() { Trigger = "tx", Name = "TRANSACTION", Category = "Safety", Description = "Run work inside a transaction", Content = "BEGIN TRANSACTION;\n\n${cursor}\n\nCOMMIT TRANSACTION;" },
        new() { Trigger = "paste", Name = "Paste clipboard", Category = "Utility", Description = "Insert clipboard contents", Content = "${cursor}" },
    ];

    public static IReadOnlyList<SnippetDef> ForDriver(string driver) =>
        All.Where(s => s.Drivers is null || s.Drivers.Contains(driver)).ToList();
}
