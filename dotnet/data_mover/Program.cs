using data_mover.ColumnProcessors;
using Npgsql;
using Npgsql.Schema;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;
using Tomlyn;
using Tomlyn.Model;

namespace data_mover;

static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var config = ReadConfig(args);
        if (config is null)
        {
            PrintUsage();
            return 1;
        }

        var sourceDbConfig = DbConfig.ReadConfigFrom(config, "source");
        var destinationDbConfig = DbConfig.ReadConfigFrom(config, "destination");

        // get list of tables and columns to process
        var tablesToProcess = ReadTables(config);
        var columnsToProcess = ReadColumns(config);

        // truncate target tables
        TruncateDestinationDatabaseTables(tablesToProcess, destinationDbConfig);

        // process the tables.
        await ProcessTables(tablesToProcess, columnsToProcess, sourceDbConfig, destinationDbConfig);

        return 0;
    }

    private static async Task ProcessTables(IReadOnlyList<TableConfiguration> tablesToProcess, IReadOnlyDictionary<DatabaseColumn, IColumnProcessor> columnsToProcess, DbConfig sourceDbConfig, DbConfig destinationDbConfig)
    {
        var totalStopWatch = Stopwatch.StartNew();
        foreach (var table in tablesToProcess)
        {
            var columns = columnsToProcess.Where(c => c.Key.Table == table.Table).ToFrozenDictionary();
            await ProcessTable(table, columns, sourceDbConfig, destinationDbConfig);
        }
        Console.WriteLine("Total time processing tables: " + totalStopWatch.Elapsed.TotalSeconds + "s");
    }

    private static async Task ProcessTable(TableConfiguration table, IReadOnlyDictionary<DatabaseColumn, IColumnProcessor> columnsToProcess, DbConfig sourceDbConfig, DbConfig destinationDbConfig)
    {
        using var sourceConnection = sourceDbConfig.Connection();
        sourceConnection.Open();
        using var destinationConnection = destinationDbConfig.Connection();
        destinationConnection.Open();

        var query = table.Limit is null
            ? "SELECT * FROM " + table.Table
            : "SELECT * FROM " + table.Table + " LIMIT " + table.Limit;
        using var readCommand = sourceConnection.CreateCommand();
        readCommand.CommandText = query;
        using var reader = readCommand.ExecuteReader();
        var tablesColumns = reader.GetColumnSchema().ToImmutableArray();

        Console.WriteLine($"Starting table {table.Table}.");
        var sinceLastReport = Stopwatch.StartNew();
        var totalTime = Stopwatch.StartNew();

        var rows = new List<Dictionary<string, object>>();

        var readerChannel = Channel.CreateUnbounded<List<Dictionary<string, object>>>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true
        });

        var writerChanel = Channel.CreateUnbounded<List<Dictionary<string, object>>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var cts = new CancellationTokenSource();
        var token = cts.Token;

        var readerTask = Task.Run(() => BackgroundReaderRowsService(reader, tablesColumns, readerChannel, token));
        var processorTask = Task.Run(() => BackgroundProcessRowsService(readerChannel, writerChanel, columnsToProcess, token));
        var writerTask = Task.Run(() => BackgroundWriteRowsService(writerChanel, table.Table, tablesColumns, destinationConnection, sinceLastReport, totalTime, token));

        await Task.WhenAll(readerTask, processorTask, writerTask);

        Console.WriteLine($"Table {table} processed in: " + totalTime.Elapsed.TotalSeconds + "s");
    }    

    private static async Task BackgroundReaderRowsService(
        NpgsqlDataReader reader, ImmutableArray<NpgsqlDbColumn> tablesColumns, Channel<List<Dictionary<string, object>>> readerChannel, CancellationToken token)
    {
        int batchSize = 500;
        var endOfRead = false;

        while (!endOfRead)
        {
            var batchOfRows = new List<Dictionary<string, object>>();

            for (int i = 0; i < batchSize; i++)
            {
                if (!reader.Read())
                {
                    endOfRead = true;
                    break;
                }
                batchOfRows.Add(ReadRow(reader, tablesColumns));
            }

            await readerChannel.Writer.WriteAsync(batchOfRows, token);
        }

        readerChannel.Writer.Complete();
    }

    private static async Task BackgroundProcessRowsService(
        Channel<List<Dictionary<string, object>>> readerChannel,
        Channel<List<Dictionary<string, object>>> writerChanel,
        IReadOnlyDictionary<DatabaseColumn,
        IColumnProcessor> columnsToProcess,
        CancellationToken token)
    {
        await Parallel.ForEachAsync(readerChannel.Reader.ReadAllAsync(token), async (batchOfRows, token) =>
        {
            foreach (var row in batchOfRows)
            {
                ProcessRow(row, columnsToProcess);
            }
            await writerChanel.Writer.WriteAsync(batchOfRows, token);
        });

        writerChanel.Writer.Complete();
    }

    private static async Task BackgroundWriteRowsService(
        Channel<List<Dictionary<string, object>>> writerChanel,
        DatabaseTable table,
        ImmutableArray<NpgsqlDbColumn> tablesColumns,
        NpgsqlConnection destinationConnection,
        Stopwatch sinceLastReport,
        Stopwatch totalTime,
        CancellationToken token)
    {
        var rows = new List<Dictionary<string, object>>();

        await foreach (var batchOfRows in writerChanel.Reader.ReadAllAsync())
        {
            rows.AddRange(batchOfRows);
        }

        await WriteRows(rows, table, tablesColumns, destinationConnection, sinceLastReport, totalTime);
    }

    private static Dictionary<string, object> ReadRow(NpgsqlDataReader reader, IReadOnlyList<NpgsqlDbColumn> columns)
    {
        var retval = new Dictionary<string, object>(columns.Count);
        foreach (var column in columns)
        {
            int ordinal = (int) column.ColumnOrdinal!;
            retval[column.ColumnName] = reader.GetValue(ordinal);
        }

        return retval;
    }

    private static void ProcessRow(Dictionary<string, object> row, IReadOnlyDictionary<DatabaseColumn, IColumnProcessor> columnsToProcess)
    {
        foreach (var column in columnsToProcess)
        {
            var columnName = column.Key.Column;
            row[columnName] = column.Value.ProcessValue(row[columnName]);
        }
    }

    private static async Task WriteRows(IEnumerable<Dictionary<string, object>> rows, DatabaseTable table, IReadOnlyList<NpgsqlDbColumn> columns, NpgsqlConnection destinationConnection, Stopwatch sinceLastReport, Stopwatch totalTime)
    {
        long count = 0;

        using var writer = destinationConnection.BeginBinaryImport($"COPY {table} ({string.Join(", ", columns.Select(c => c.ColumnName))}) FROM STDIN (FORMAT BINARY)");

        foreach (var row in rows)
        {
            writer.StartRow();
            foreach (var column in columns)
            {
                writer.Write(row[column.ColumnName]);
            }

            count += 1;
            if (sinceLastReport.Elapsed.TotalSeconds > 10)
            {
                Console.WriteLine($"Processing {table.Table} for {totalTime.Elapsed.TotalSeconds}s at row number {count}.");
                sinceLastReport.Restart();
            }
        }

        await writer.CompleteAsync();
    }

    private static void TruncateDestinationDatabaseTables(IReadOnlyList<TableConfiguration> tablesToProcess, DbConfig destinationDbConfig)
    {
        foreach (var table in tablesToProcess)
        {
            TruncateTable(table.Table, destinationDbConfig);
        }
    }

    private static void TruncateTable(DatabaseTable table, DbConfig destinationDbConfig)
    {
        using var connection = destinationDbConfig.Connection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "TRUNCATE TABLE " + table.ToString();
        command.ExecuteNonQuery();
    }


    private static TomlTable? ReadConfig(string[] args)
    {
        try
        {
            var path = args[0];
            if (!File.Exists(path))
                return null;

            var fileContent = File.ReadAllText(path);
            return Toml.ToModel(fileContent, path);
        }
        catch (IndexOutOfRangeException)
        {
            return null;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            return null;
        }
    }

    private static IReadOnlyList<TableConfiguration> ReadTables(TomlTable config)
    {
        var retval = new List<TableConfiguration>();

        var tableArray = config["table"] as TomlTableArray;
        if (tableArray is null)
        {
            throw new ArgumentException("Array of 'table's is missing from config");
        }

        foreach (var table in tableArray)
        {
            var schemaName = table["schemaName"] as string;
            var tableName = table["tableName"] as string;
            var limit = table.ContainsKey("limit") ? table["limit"] as long? : null;
            if (schemaName is null || tableName is null)
            {
                throw new ArgumentException("Table must contain a 'schemaName' and 'tableName' property");
            }

            retval.Add(new TableConfiguration(new DatabaseTable(schemaName, tableName), limit));
        }

        return retval.ToImmutableArray();
    }

    private static IReadOnlyDictionary<DatabaseColumn, IColumnProcessor> ReadColumns(TomlTable config)
    {
        var retval = new Dictionary<DatabaseColumn, IColumnProcessor>();

        if (!config.ContainsKey("column"))
        {
            // no columns, then return empty config
            return retval;
        }

        var columnArray = config["column"] as TomlTableArray;
        if (columnArray is null)
        {
            throw new ArgumentException("Array of 'table's is missing from config");
        }

        foreach (var column in columnArray)
        {
            var schemaName = column["schemaName"] as string;
            var tableName = column["tableName"] as string;
            var columnName = column["columnName"] as string;
            var processor = column["processor"] as string;
            if (schemaName is null || tableName is null || columnName is null || processor is null)
            {
                throw new ArgumentException("Column must contain a 'schemaName', 'tableName','columnName' and 'processor', property");
            }

            var table = new DatabaseTable(schemaName, tableName);
            var columnProcessor = ColumnProcessors.ColumnProcessors.Create(processor);
            retval.Add(new DatabaseColumn(table, columnName), columnProcessor);
        }

        return retval.ToFrozenDictionary();
    }


    private static void PrintUsage()
    {
        Console.Error.WriteLine("Need to supply a TOML file configuration.");
        Console.Error.WriteLine("dotnet data_mover.dll <configuration.toml>");
    }
}