using System.Diagnostics;
using System.Globalization;
using System.Text;
using Larik.Data;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Larik.Data.MongoDB;

/// <summary>
/// MongoDB provider. One <see cref="MongoClient"/> is a connection session; the shell and editor talk
/// only to the contracts, and this assembly owns the SQL-on-MongoDB translation. Nothing in the app
/// knows MongoDB exists.
/// </summary>
public sealed class MongoProvider : IDatabaseProvider
{
    public const string ProviderId = "mongodb";

    public string Id => ProviderId;

    public string DisplayName => "MongoDB";

    public string Dialect => ProviderId;

    public IEditorLanguage? EditorLanguage => MongoTSqlDialect.Instance;

    public string? DescribeEndpoint(ConnectionProfile profile)
    {
        var host = profile.Get("host");
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var port = profile.GetInt("port", 27017);
        return $"MongoDB · {host}:{port}";
    }

    public string? DescribeIdentity(ConnectionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.Get("user")) ? null : profile.Get("user")!.Trim();

    public IReadOnlyList<ProviderSetting> Settings { get; } =
    [
        new()
        {
            Key = "host",
            Label = "Host",
            Required = true,
            DefaultValue = "localhost",
            Placeholder = "localhost or a host name",
        },
        new()
        {
            Key = "port",
            Label = "Port",
            Kind = ProviderSettingKind.Number,
            DefaultValue = "27017",
            IsAdvanced = true,
        },
        new()
        {
            Key = "database",
            Label = "Database",
            Placeholder = "the default database to start in",
        },
        new()
        {
            Key = "authSource",
            Label = "Auth source",
            DefaultValue = "admin",
            Placeholder = "admin",
            IsAdvanced = true,
        },
        new() { Key = "user", Label = "User" },
        new() { Key = "password", Label = "Password", Kind = ProviderSettingKind.Password },
        new()
        {
            Key = "directConnection",
            Label = "Direct connection",
            Kind = ProviderSettingKind.Boolean,
            DefaultValue = "true",
            IsAdvanced = true,
            Hint = "Connect to a single node instead of a replica-set discovery scan.",
        },
        new()
        {
            Key = "connectTimeout",
            Label = "Connect timeout (s)",
            Kind = ProviderSettingKind.Number,
            DefaultValue = "10",
            IsAdvanced = true,
        },
        new()
        {
            Key = "queryTimeout",
            Label = "Query timeout (s)",
            Kind = ProviderSettingKind.Number,
            DefaultValue = "60",
            Hint = "How long a statement may run before the client cancels it. 0 waits indefinitely.",
            IsAdvanced = true,
        },
    ];

    public IDatabaseConnection CreateConnection(ConnectionProfile profile) => new MongoConnection(profile);
}

/// <summary>
/// One MongoDB session, held per query tab. All session state (the active database, the client) lives
/// here, never in a shared static, so a <c>USE</c> in one tab never leaks into another.
/// </summary>
internal sealed class MongoConnection : IDatabaseConnection
{
    private readonly ConnectionProfile _profile;
    private readonly string _profileDatabase;

    // Sampled field types per (database, collection), used to coerce string literals to UUID/ObjectId
    // binaries. Session-scoped: the schema is inferred, so it can go stale, and a reconnect starts fresh.
    private readonly Dictionary<string, Dictionary<string, string>> _fieldTypes = new(StringComparer.Ordinal);
    private MongoClient? _client;
    private string _activeDatabase;
    private string? _version;

    public MongoConnection(ConnectionProfile profile)
    {
        _profile = profile;
        _profileDatabase = string.IsNullOrWhiteSpace(profile.Get("database")) ? "admin" : profile.Get("database")!.Trim();
        _activeDatabase = _profileDatabase;
    }

    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;

    public string? ServerVersion => _version;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        var client = new MongoClient(BuildSettings());
        try
        {
            State = ConnectionState.Connecting;
            // Ping is the cheap, reliable "can I talk to the server" probe.
            var admin = client.GetDatabase("admin");
            await admin.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("ping", 1)), cancellationToken: cancellationToken);

            try
            {
                var buildInfo = await admin.RunCommandAsync(new BsonDocumentCommand<BsonDocument>(new BsonDocument("buildInfo", 1)), cancellationToken: cancellationToken);
                _version = buildInfo.TryGetValue("version", out var v) ? $"MongoDB {v.AsString}" : "MongoDB";
            }
            catch
            {
                _version = "MongoDB";
            }

            _client = client;
            State = ConnectionState.Connected;
        }
        catch
        {
            State = ConnectionState.Faulted;
            throw;
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_client is { } client)
        {
            try
            {
                client.Dispose();
            }
            catch
            {
                // Closing is best effort; a broken client must not block the shell.
            }

            _client = null;
        }

        _version = null;
        State = ConnectionState.Disconnected;
        _fieldTypes.Clear();
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await CloseAsync();

    private MongoClientSettings BuildSettings()
    {
        var host = _profile.Get("host");
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("The MongoDB connection needs a host name.");
        }

        var port = _profile.GetInt("port", 27017);
        var settings = new MongoClientSettings
        {
            Server = new MongoServerAddress(host, port is > 0 and <= 65535 ? port : 27017),
            DirectConnection = _profile.GetBool("directConnection", true),
            ConnectTimeout = TimeSpan.FromSeconds(Math.Max(1, _profile.GetInt("connectTimeout", 10))),
            ServerSelectionTimeout = TimeSpan.FromSeconds(Math.Max(1, _profile.GetInt("connectTimeout", 10))),
            ApplicationName = "Larik SQL Studio",
            MaxConnectionPoolSize = 1,
        };

        var user = _profile.Get("user");
        var password = _profile.Get("password");
        if (!string.IsNullOrWhiteSpace(user))
        {
            settings.Credential = MongoCredential.CreateCredential(
                string.IsNullOrWhiteSpace(_profile.Get("authSource")) ? "admin" : _profile.Get("authSource")!.Trim(),
                user.Trim(),
                password ?? string.Empty);
        }

        return settings;
    }

    private string ResolveDb(string? requested) =>
        string.IsNullOrWhiteSpace(requested) ? _activeDatabase : requested.Trim();

    // ------------------------------------------------------------------ metadata

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken);
        var names = new List<string>();
        using var cursor = await _client!.ListDatabaseNamesAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            names.AddRange(cursor.Current);
        }

        return names;
    }

    public Task<string?> GetCurrentDatabaseAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(State == ConnectionState.Connected ? _activeDatabase : null);

    public async Task<IReadOnlyList<TableSchema>> GetSchemaAsync(string? database, CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken);
        var db = _client!.GetDatabase(ResolveDb(database));
        var collections = new List<string>();
        using var cursor = await db.ListCollectionNamesAsync(cancellationToken: cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            collections.AddRange(cursor.Current);
        }

        var schemas = new List<TableSchema>(collections.Count);
        foreach (var name in collections)
        {
            schemas.Add(await SampleCollectionAsync(db, name, cancellationToken));
        }

        return schemas;
    }

    /// <summary>
    /// Infers a collection's fields and types from a small random sample, so completion and the schema
    /// browser have something real even though MongoDB has no fixed schema. An empty (or permission
    /// denied) collection simply yields the name.
    /// </summary>
    private static async Task<TableSchema> SampleCollectionAsync(IMongoDatabase db, string name, CancellationToken cancellationToken)
    {
        var columns = new List<string>();
        var types = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            var collection = db.GetCollection<BsonDocument>(name);
            var sample = await collection
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Limit(200)
                .ToListAsync(cancellationToken);

            foreach (var doc in sample)
            {
                CollectFields(doc, "", columns, types);
            }
        }
        catch
        {
            // Schema is a browsing nicety; a failure must not break the session.
        }

        return new TableSchema
        {
            Name = name,
            Schema = null,
            Columns = columns,
            ColumnDetails = columns.Select(c => new TableColumn { Name = c, DataType = types.GetValueOrDefault(c) }).ToList(),
            PrimaryKey = ["_id"],
            Kind = TableKind.Table,
        };
    }

    private static void CollectFields(BsonDocument doc, string prefix, List<string> columns, Dictionary<string, string> types)
    {
        foreach (var element in doc.Elements)
        {
            var name = prefix.Length == 0 ? element.Name : $"{prefix}.{element.Name}";
            if (!types.ContainsKey(name))
            {
                columns.Add(name);
                types[name] = BsonTypeName(element.Value);
            }

            // Nested sub-documents contribute dotted paths for completion and browsing.
            if (element.Value is BsonDocument nested)
            {
                CollectFields(nested, name, columns, types);
            }
        }
    }

    private static string BsonTypeName(BsonValue value) => value.BsonType switch
    {
        BsonType.String => "string",
        BsonType.Int32 or BsonType.Int64 => "int64",
        BsonType.Double or BsonType.Decimal128 => "double",
        BsonType.Boolean => "bool",
        BsonType.DateTime => "date",
        BsonType.ObjectId => "objectId",
        BsonType.Binary => value.AsBsonBinaryData.SubType is BsonBinarySubType.UuidStandard or BsonBinarySubType.UuidLegacy
            ? "uuid"
            : "binary",
        BsonType.Array => "array",
        BsonType.Document => "object",
        BsonType.Null => "null",
        _ => value.BsonType.ToString().ToLowerInvariant(),
    };

    /// <summary>Sampled field types for one collection, cached for the session.</summary>
    private async Task<IReadOnlyDictionary<string, string>> GetFieldTypesAsync(string collection, CancellationToken cancellationToken)
    {
        var key = _activeDatabase + "\u001F" + collection;
        if (_fieldTypes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var schema = await SampleCollectionAsync(Database(), collection, cancellationToken);
            foreach (var column in schema.ColumnDetails)
            {
                if (!string.IsNullOrEmpty(column.DataType))
                {
                    map[column.Name] = column.DataType;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Writes and empty collections are fine without type info.
        }

        _fieldTypes[key] = map;
        return map;
    }

    // ------------------------------------------------------------------ execution

    public Task<QueryResult> ExecuteAsync(string sql, string? database, CancellationToken cancellationToken = default) =>
        ExecuteAsync(sql, database, QueryExecutionOptions.Default, cancellationToken);

    public async Task<QueryResult> ExecuteAsync(
        string sql,
        string? database,
        QueryExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        var results = await ExecuteBatchAsync(sql, database, options, cancellationToken);
        return results.Count > 0
            ? results[0]
            : QueryResult.Error("No result returned.", TimeSpan.Zero, database);
    }

    /// <summary>
    /// Mongodb has no <c>GO</c>; the editor sends the document as one batch. Execution still splits it
    /// on semicolons (using the editor language the user actually sees, which tags mongosh passthrough
    /// statements), so each statement reports separately and a failure is attributable to the one that
    /// caused it.
    /// </summary>
    public async Task<IReadOnlyList<QueryResult>> ExecuteBatchAsync(
        string sql,
        string? database,
        QueryExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        await EnsureOpenAsync(cancellationToken);

        // The shell passes the tab's current database; adopt it so a database switch in the UI
        // takes effect before the first statement. A `USE` inside the batch then overrides it.
        if (!string.IsNullOrWhiteSpace(database))
        {
            _activeDatabase = database.Trim();
        }

        var stopwatch = Stopwatch.StartNew();
        var spans = MongoTSqlDialect.Instance.Split(sql);
        if (spans.Count == 0)
        {
            return [QueryResult.Error("Nothing to run — the editor is empty.", stopwatch.Elapsed, database)];
        }

        var results = new List<QueryResult>(spans.Count);
        foreach (var span in spans)
        {
            var statement = sql.Substring(span.Start, span.Length);
            QueryResult result;
            if (span.Language == MongoScriptClassifier.Shell)
            {
                result = await ExecuteShellAsync(statement, stopwatch, cancellationToken);
            }
            else
            {
                result = await ExecuteSqlAsync(statement, options, stopwatch, cancellationToken);
            }

            results.Add(result);
            if (result.IsError)
            {
                break;
            }
        }

        return results;
    }

    private async Task<QueryResult> ExecuteSqlAsync(
        string statement,
        QueryExecutionOptions options,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        MongoCommand command;
        try
        {
            command = TSqlMongoCompiler.Parse(statement);
        }
        catch (TranslationException ex)
        {
            return QueryResult.Error(ex.Message, stopwatch.Elapsed, _activeDatabase);
        }

        // MongoDB compares BSON types strictly, so `WHERE _id = 'guid'` needs the value built as a
        // UUID binary. Translate once to learn the collection, sample its field types (cached), then
        // translate again with those types. Sampling is a nicety: a failure just leaves the string.
        var collection = command switch
        {
            MSelect select => select.Collection,
            MUpdate update => update.Collection,
            MDelete delete => delete.Collection,
            _ => null,
        };

        if (!string.IsNullOrWhiteSpace(collection))
        {
            try
            {
                var types = await GetFieldTypesAsync(collection, cancellationToken);
                if (types.Count > 0)
                {
                    command = TSqlMongoCompiler.Parse(statement, types);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Type-aware coercion is best effort; the plain literal still runs.
            }
        }

        try
        {
            return command switch
            {
                MSelect select => RunSelect(select, options, stopwatch, cancellationToken),
                MInsert insert => RunInsert(insert, stopwatch, cancellationToken),
                MUpdate update => RunUpdate(update, stopwatch, cancellationToken),
                MDelete delete => RunDelete(delete, stopwatch, cancellationToken),
                MUse use => UseDatabase(use, stopwatch),
                _ => QueryResult.Error("Unsupported statement.", stopwatch.Elapsed, _activeDatabase),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return QueryResult.Error(ex.Message, stopwatch.Elapsed, _activeDatabase);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private QueryResult RunSelect(MSelect select, QueryExecutionOptions options, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var collection = Database().GetCollection<BsonDocument>(select.Collection);

        // Enforce the provider row cap without materialising an unbounded result: append a limit of
        // EffectiveMaxRows+1 unless the user already set one.
        var pipeline = new List<BsonDocument>(select.Pipeline);
        var rowCap = options.EffectiveMaxRows;
        // Always enforce the provider cap. A user LIMIT smaller than the cap is harmless here;
        // one larger than the cap is clipped, and the extra row tells us it was truncated.
        pipeline.Add(new BsonDocument("$limit", rowCap + 1));

        var docs = collection.Aggregate<BsonDocument>(pipeline).ToList(cancellationToken);

        var truncated = docs.Count > rowCap;
        var rows = truncated ? docs.Take(rowCap).ToList() : docs;

        var columns = select.Columns ?? InferColumns(rows);
        var resultRows = rows.Select(doc => columns.Select(c => (string?)FormatCell(doc.GetValue(c, BsonNull.Value))).ToArray()).ToList();

        var generated = string.Join(Environment.NewLine, select.Pipeline.Select(p => p.ToJson(new JsonWriterSettings { Indent = true })));
        return new QueryResult
        {
            Columns = columns.Select(c => new QueryColumn { Name = c }).ToList(),
            Rows = resultRows,
            Message = $"{resultRows.Count} rows",
            Elapsed = stopwatch.Elapsed,
            Database = _activeDatabase,
            Statement = select.Original,
            GeneratedQuery = generated,
            IsTruncated = truncated,
            RowLimit = rowCap,
        };
    }

    private static IReadOnlyList<string> InferColumns(List<BsonDocument> docs)
    {
        var columns = new List<string>();
        foreach (var doc in docs)
        {
            foreach (var element in doc.Elements)
            {
                if (!columns.Contains(element.Name))
                {
                    columns.Add(element.Name);
                }
            }
        }

        return columns;
    }

    private QueryResult RunInsert(MInsert insert, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var collection = Database().GetCollection<BsonDocument>(insert.Collection);
        collection.InsertMany(insert.Documents, cancellationToken: cancellationToken);
        return new QueryResult
        {
            Columns = [],
            Rows = [],
            RecordsAffected = insert.Documents.Count,
            Message = $"{insert.Documents.Count} document(s) inserted",
            Elapsed = stopwatch.Elapsed,
            Database = _activeDatabase,
            Statement = insert.Original,
        };
    }

    private QueryResult RunUpdate(MUpdate update, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var collection = Database().GetCollection<BsonDocument>(update.Collection);
        var result = collection.UpdateMany(update.Filter, new BsonDocument("$set", update.Update), cancellationToken: cancellationToken);
        var affected = (int)result.ModifiedCount;
        return new QueryResult
        {
            Columns = [],
            Rows = [],
            RecordsAffected = affected,
            Message = $"{affected} document(s) updated",
            Elapsed = stopwatch.Elapsed,
            Database = _activeDatabase,
            Statement = update.Original,
        };
    }

    private QueryResult RunDelete(MDelete delete, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var collection = Database().GetCollection<BsonDocument>(delete.Collection);

        // Refuse a blanket deleteUnless the user really asked for everything — the translator already
        // allows "delete all", but here we can tell whether a filter was present at all.
        if (delete.Filter.ElementCount == 0)
        {
            return QueryResult.Error(
                "DELETE without a WHERE would remove every document; add a WHERE clause (or mongosh db.collection.deleteMany({}) to confirm).",
                stopwatch.Elapsed,
                _activeDatabase);
        }

        var result = collection.DeleteMany(delete.Filter, cancellationToken);
        var affected = (int)result.DeletedCount;
        return new QueryResult
        {
            Columns = [],
            Rows = [],
            RecordsAffected = affected,
            Message = $"{affected} document(s) deleted",
            Elapsed = stopwatch.Elapsed,
            Database = _activeDatabase,
            Statement = delete.Original,
        };
    }

    private QueryResult UseDatabase(MUse use, Stopwatch stopwatch)
    {
        _activeDatabase = use.Database;
        return new QueryResult
        {
            Columns = [],
            Rows = [],
            Message = $"Using {use.Database}.",
            Elapsed = stopwatch.Elapsed,
            Database = _activeDatabase,
            Statement = use.Original,
        };
    }

    private IMongoDatabase Database() => _client!.GetDatabase(_activeDatabase);

    private async Task EnsureOpenAsync(CancellationToken cancellationToken)
    {
        if (State != ConnectionState.Connected)
        {
            await OpenAsync(cancellationToken);
        }
    }

    // ------------------------------------------------------------------ mongosh passthrough

    /// <summary>Runs a <c>db.collection.method(...)</c> statement as written, for the common methods.</summary>
    private async Task<QueryResult> ExecuteShellAsync(string statement, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        try
        {
            var shell = MongoShellRunner.Parse(statement);
            return shell switch
            {
                ShellFind f => await RunShellFind(f, stopwatch, cancellationToken),
                ShellFindOne f => await RunShellFindOne(f, stopwatch, cancellationToken),
                ShellCount c => await RunShellCount(c, stopwatch, cancellationToken),
                ShellInsertMany i => RunShellInsert(i, stopwatch, cancellationToken),
                ShellUpdate u => await RunShellUpdate(u, stopwatch, cancellationToken),
                ShellDeleteBatch d => await RunShellDelete(d, stopwatch, cancellationToken),
                _ => QueryResult.Error("This mongosh form is not supported yet — use the SQL surface.", stopwatch.Elapsed, _activeDatabase),
            };
        }
        catch (TranslationException ex)
        {
            return QueryResult.Error(ex.Message, stopwatch.Elapsed, _activeDatabase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return QueryResult.Error(ex.Message, stopwatch.Elapsed, _activeDatabase);
        }
    }

    private async Task<QueryResult> RunShellFind(ShellFind find, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var collection = Database().GetCollection<BsonDocument>(find.Collection);
        var docs = await collection.Find(find.Filter).ToListAsync(cancellationToken);
        return RowsResult(docs, stopwatch);
    }

    private async Task<QueryResult> RunShellFindOne(ShellFindOne find, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var collection = Database().GetCollection<BsonDocument>(find.Collection);
        var doc = await collection.Find(find.Filter).FirstOrDefaultAsync(cancellationToken);
        var docs = new List<BsonDocument>();
        if (doc is not null)
        {
            docs.Add(doc);
        }

        return RowsResult(docs, stopwatch);
    }

    private async Task<QueryResult> RunShellCount(ShellCount count, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var collection = Database().GetCollection<BsonDocument>(count.Collection);
        var value = await collection.CountDocumentsAsync(count.Filter, cancellationToken: cancellationToken);
        var doc = new BsonDocument("count", value);
        return RowsResult([doc], stopwatch);
    }

    private QueryResult RunShellInsert(ShellInsertMany insert, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var collection = Database().GetCollection<BsonDocument>(insert.Collection);
        collection.InsertMany(insert.Documents, cancellationToken: cancellationToken);
        return new QueryResult
        {
            Columns = [],
            Rows = [],
            RecordsAffected = insert.Documents.Count,
            Message = $"{insert.Documents.Count} document(s) inserted",
            Elapsed = stopwatch.Elapsed,
            Database = _activeDatabase,
        };
    }

    private async Task<QueryResult> RunShellUpdate(ShellUpdate update, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var collection = Database().GetCollection<BsonDocument>(update.Collection);
        var result = collection.UpdateMany(update.Filter, update.Update, cancellationToken: cancellationToken);
        return new QueryResult
        {
            Columns = [],
            Rows = [],
            RecordsAffected = (int)result.ModifiedCount,
            Message = $"{result.ModifiedCount} document(s) updated",
            Elapsed = stopwatch.Elapsed,
            Database = _activeDatabase,
        };
    }

    private async Task<QueryResult> RunShellDelete(ShellDeleteBatch delete, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var collection = Database().GetCollection<BsonDocument>(delete.Collection);
        var result = await collection.DeleteManyAsync(delete.Filter, cancellationToken);
        return new QueryResult
        {
            Columns = [],
            Rows = [],
            RecordsAffected = (int)result.DeletedCount,
            Message = $"{result.DeletedCount} document(s) deleted",
            Elapsed = stopwatch.Elapsed,
            Database = _activeDatabase,
        };
    }

    private static QueryResult RowsResult(List<BsonDocument> docs, Stopwatch stopwatch)
    {
        var columns = InferColumns(docs);
        var rows = docs.Select(doc => columns.Select(c => (string?)FormatCell(doc.GetValue(c, BsonNull.Value))).ToArray()).ToList();
        return new QueryResult
        {
            Columns = columns.Select(c => new QueryColumn { Name = c }).ToList(),
            Rows = rows,
            Message = $"{rows.Count} rows",
            Elapsed = stopwatch.Elapsed,
        };
    }

    private static string? FormatCell(BsonValue value) => MongoValueFormatter.Format(value);
}

// ============================================================================
// mongosh passthrough shapes
// ============================================================================

internal abstract record ShellCommand;
internal sealed record ShellFind(string Collection, BsonDocument Filter) : ShellCommand;
internal sealed record ShellFindOne(string Collection, BsonDocument Filter) : ShellCommand;
internal sealed record ShellCount(string Collection, BsonDocument Filter) : ShellCommand;
internal sealed record ShellInsertMany(string Collection, List<BsonDocument> Documents) : ShellCommand;
internal sealed record ShellUpdate(string Collection, BsonDocument Filter, BsonDocument Update) : ShellCommand;
internal sealed record ShellDeleteBatch(string Collection, BsonDocument Filter) : ShellCommand;

internal static class MongoShellRunner
{
    /// <summary>
    /// Parses <c>db.collection.method(...)</c> for the common driver methods. Collection and method
    /// come from the literal text; the argument (an extended-JSON document or array) is parsed with the
    /// driver. Advanced mongosh (ObjectId('…') expressions, chained cursor methods) is not parsed;
    /// those fall back to an honest "use the SQL surface" error.
    /// </summary>
    public static ShellCommand Parse(string statement)
    {
        var text = statement.Trim().TrimEnd(';').Trim();
        if (!text.StartsWith("db.", StringComparison.OrdinalIgnoreCase))
        {
            throw new TranslationException("Mongosh passthrough must start with db.");
        }

        var open = text.IndexOf('(');
        if (open < 0 || !text.EndsWith(')'))
        {
            throw new TranslationException("Mongosh passthrough needs a call like db.collection.method(…).");
        }

        var head = text[3..open]; // collection.method, after "db."
        var lastDot = head.LastIndexOf('.');
        if (lastDot <= 0)
        {
            throw new TranslationException("Mongosh passthrough must name a collection and a method (db.collection.method(…)).");
        }

        var collection = head[..lastDot].Trim();
        var method = head[(lastDot + 1)..].Trim().ToLowerInvariant();
        var body = text[(open + 1)..^1].Trim();

        switch (method)
        {
            case "find":
                return new ShellFind(collection, ArgDoc(body, 0));
            case "findone":
                return new ShellFindOne(collection, ArgDoc(body, 0));
            case "count":
            case "countdocuments":
                return new ShellCount(collection, ArgDoc(body, 0));
            case "insertone":
            case "insertmany":
                return new ShellInsertMany(collection, InsertDocs(body, method == "insertone"));
            case "updateone":
            case "updatemany":
                return ParseUpdate(collection, body);
            case "deleteone":
            case "deletemany":
                return new ShellDeleteBatch(collection, ArgDoc(body, 0));
            default:
                throw new TranslationException($"Mongosh method '{method}()' is not supported — use the SQL surface.");
        }
    }

    private static BsonDocument ArgDoc(string body, int index)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new BsonDocument();
        }

        var args = SplitArgs(body);
        if (index >= args.Count)
        {
            return new BsonDocument();
        }

        return BsonDocument.Parse(args[index]);
    }

    private static List<BsonDocument> InsertDocs(string body, bool single)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new TranslationException("insert needs a document.");
        }

        var args = SplitArgs(body);
        if (single)
        {
            return [BsonDocument.Parse(args[0])];
        }

        // insertMany takes an array of documents; wrap it in an object so the driver's JSON reader
// accepts a bare array and we can lift it back out.
        var wrapped = BsonDocument.Parse("{ \"__array\": " + args[0] + " }");
        return wrapped["__array"].AsBsonArray.Values.Select(v => v.AsBsonDocument).ToList();
    }

    private static ShellUpdate ParseUpdate(string collection, string body)
    {
        var args = SplitArgs(body);
        if (args.Count < 2)
        {
            throw new TranslationException("update needs (filter, update).");
        }

        return new ShellUpdate(collection, BsonDocument.Parse(args[0]), BsonDocument.Parse(args[1]));
    }

    /// <summary>Splits a positional argument list, shallow (no nesting-aware split needed for the common methods).</summary>
    private static List<string> SplitArgs(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var args = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '"' && (i == 0 || body[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (!inString && c is '[' or '{' or '(')
            {
                depth++;
            }
            else if (!inString && c is ']' or '}' or ')')
            {
                depth--;
            }
            else if (!inString && c == ',' && depth == 0)
            {
                args.Add(body[start..i].Trim());
                start = i + 1;
            }
        }

        args.Add(body[start..].Trim());
        return args.Where(a => a.Length > 0).ToList();
    }
}