// XLocationMigration.cs
// Mock migration of STWH.csv (warehouses) into XLOCATION.
//
// Source: STWH.csv, comma delimited, values quoted with ", header row:
//   "WHCODE","WHNAME","WHCTRY"
// Target: XLocationTable (XLocationRow), defined in POCOs.cs.
//
// Per row: read the raw values, trim leading/trailing whitespace, validate
// against the XLOCATION schema, then insert. Rows that fail are logged as
// skipped with a tag and never inserted. Duplicate location codes are handled
// by XLocationTable.ResolveDuplicate.
//
// Exceptions are thrown only for an unusable file (empty, wrong header) and
// for a reconciliation imbalance, which would be a bug in this code.
//
// Requires the CsvHelper NuGet package: dotnet add package CsvHelper

using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using Migration.Model;

namespace Migration.Load;

public static class XLocationMigration
{
    private const string SourceTable = "STWH";

    // Expected source columns, in file order. The header is checked before any
    // row is read, so these positions are guaranteed for every row.
    private static readonly string[] ExpectedHeader = ["WHCODE", "WHNAME", "WHCTRY"];
    private const int WhCode = 0;
    private const int WhName = 1;
    private const int WhCtry = 2;

    /// <summary>Reads STWH.csv and inserts one XLocationRow per valid source row.</summary>
    /// <returns>The number of data rows read from the source (header excluded).</returns>
    public static int Migrate(string path, StagingTables tables, Encoding? encoding = null)
    {
        // Set by CsvHelper when a row has malformed quoting. Malformed rows are bad
        // data, not an exception: they are flagged here and skipped in the loop.
        bool currentRowHasBadQuotes = false;

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ",",
            Quote = '"',
            HasHeaderRecord = true,
            BadDataFound = _ => currentRowHasBadQuotes = true,
            // Field counts are checked per row in the loop instead of throwing.
            DetectColumnCountChanges = false,
            MissingFieldFound = null,
            // Whitespace is trimmed by this code, so the raw row stays available for the log.
            TrimOptions = TrimOptions.None,
        };

        using var reader = new StreamReader(path, encoding ?? Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        ReadAndCheckHeader(csv, path);
        currentRowHasBadQuotes = false;

        int sourceRows = 0;

        // csv.Read() moves to the next row and answers "was there one?",
        // the same Try-pattern as TryParse.
        while (csv.Read())
        {
            sourceRows++;

            // Reading the fields is what makes CsvHelper parse them, so the
            // bad-quotes flag is only reliable after this line.
            string[] fields = csv.Parser.Record ?? [];
            bool rowHasBadQuotes = currentRowHasBadQuotes;
            currentRowHasBadQuotes = false;

            // The row exactly as it appears in the file, quotes included, without the line break.
            string rawRow = csv.Parser.RawRecord.TrimEnd('\r', '\n');
            var source = new SourceRef(SourceTable, sourceRows, KeyOf(fields), rawRow);

            bool fieldCountIsWrong = fields.Length != ExpectedHeader.Length;
            if (fieldCountIsWrong)
            {
                tables.Log.AddSkipped(tables.XLocation.Name, source, "STWH_FIELD_COUNT",
                    $"Row has {fields.Length} fields, expected {ExpectedHeader.Length}");
                continue;
            }

            if (rowHasBadQuotes)
            {
                tables.Log.AddSkipped(tables.XLocation.Name, source, "STWH_BAD_QUOTES",
                    "Row has malformed quoting; values cannot be trusted");
                continue;
            }

            string code = fields[WhCode].Trim();
            string name = fields[WhName].Trim();
            string country = fields[WhCtry].Trim();

            var problem = FindProblem(code, name, country);
            if (problem is not null)
            {
                tables.Log.AddSkipped(tables.XLocation.Name, source, problem.Tag, problem.Detail);
                continue;
            }

            tables.XLocation.Insert(new XLocationRow(code, name, country), source);
        }

        CheckReconciliation(tables, sourceRows);
        return sourceRows;
    }

    // ----- Header ---------------------------------------------------------------

    private static void ReadAndCheckHeader(CsvReader csv, string path)
    {
        string expected = string.Join(",", ExpectedHeader);

        bool fileHasAnyRow = csv.Read();
        if (!fileHasAnyRow)
            throw new InvalidDataException($"{path} is empty; expected header {expected}.");

        csv.ReadHeader();
        string[] header = (csv.HeaderRecord ?? []).Select(column => column.Trim()).ToArray();

        bool headerIsAsExpected = header.SequenceEqual(ExpectedHeader, StringComparer.InvariantCultureIgnoreCase);
        if (!headerIsAsExpected)
            throw new InvalidDataException(
                $"{path} has header '{string.Join(",", header)}'; expected '{expected}'.");
    }

    // ----- Validation -------------------------------------------------------------

    private sealed record Problem(string Tag, string Detail);

    /// <summary>Checks trimmed values against the XLOCATION schema. Returns the first
    /// problem found, or null if the row is valid. One tag per row keeps the
    /// log and reconciliation one-to-one with source rows; the raw row in the
    /// log shows any further problems.</summary>
    private static Problem? FindProblem(string code, string name, string country)
    {
        // Primary key: an empty code after trimming counts as null.
        if (code.Length == 0)
            return new("XLOCATION_CODE_MISSING", "WHCODE is empty");

        if (code.Length > Limits.LocationCode)
            return new("XLOCATION_CODE_TOO_LONG",
                $"WHCODE '{code}' has {code.Length} characters, maximum is {Limits.LocationCode}");

        if (name.Length == 0)
            return new("XLOCATION_NAME_MISSING", "WHNAME is empty");

        if (name.Length > Limits.LocationName)
            return new("XLOCATION_NAME_TOO_LONG",
                $"WHNAME has {name.Length} characters, maximum is {Limits.LocationName}");

        if (country.Length == 0)
            return new("XLOCATION_COUNTRY_MISSING", "WHCTRY is empty");

        if (country.Length != Limits.CountryLength)
            return new("XLOCATION_COUNTRY_INVALID",
                $"WHCTRY '{country}' is not a {Limits.CountryLength}-character country code");

        return null;
    }

    // ----- Source key ---------------------------------------------------------------

    /// <summary>The trimmed WHCODE, or "" for a row too short to have one.</summary>
    private static string KeyOf(string[] fields)
    {
        bool rowHasCodeField = fields.Length > WhCode;
        return rowHasCodeField ? fields[WhCode].Trim() : "";
    }

    // ----- Reconciliation ---------------------------------------------------------------

    /// <summary>source rows = migrated + skipped + merged, for STWH into XLOCATION.
    /// An imbalance means a row was lost by this code, which is a bug.</summary>
    private static void CheckReconciliation(StagingTables tables, int sourceRows)
    {
        string targetTable = tables.XLocation.Name;

        // STWH is the only source of XLOCATION, so every row in the table came from it.
        int migrated = tables.XLocation.Count;
        int logged = tables.Log.ForSourceTable(SourceTable)
            .Count(entry => entry.TargetTable == targetTable);

        bool balanced = sourceRows == migrated + logged;
        if (!balanced)
            throw new InvalidOperationException(
                $"{SourceTable} -> {targetTable} does not reconcile: {sourceRows} source rows, " +
                $"{migrated} migrated, {logged} skipped or merged.");
    }
}