// MigrationModel.cs
// Target row records mirroring the PostgreSQL staging schema, in-memory tables
// that enforce primary keys, and a single queryable log of skipped and merged rows.
//
// Row records are deliberately "dumb": no validation, no throwing constructors.
// All checks live in the parse methods, so a record that exists is valid.
//
// Any modification of a value on its way to the target (truncation, nulling,
// defaulting) or any rule for duplicate keys must be agreed with the client and
// documented at the point where it is applied as:
//   // DECISION D-nnn (client approved yyyy-mm-dd): <what and why>
// Until a decision is approved, the affected rows are skipped and tagged.
//
// Every source row must end up in exactly one place: a target table, or the
// run's MigrationLog (skipped or merged) for that table. For each table:
//   source rows = table.Count + log.ForTargetTable(table.Name).Count
// (filter both sides by source table when a table is fed by several sources).

namespace Migration.Model;

// ---------------------------------------------------------------------------
// Target rows (one record per staging table, column order = DDL order)
// ---------------------------------------------------------------------------

public sealed record XLocationRow(
    string LocationCode,        // VARCHAR(6)  PK
    string Name,                // VARCHAR(40) NOT NULL
    string Country);            // CHAR(3)     NOT NULL

public sealed record XPartnerRow(
    string PartnerCode,         // VARCHAR(12) PK
    string Name,                // VARCHAR(60) NOT NULL
    string? AddressLine1,       // VARCHAR(60)
    string? AddressLine2,       // VARCHAR(60)
    string Country,             // CHAR(3)     NOT NULL
    string Currency,            // CHAR(3)     NOT NULL, see Limits.Currencies
    string? VatNo,              // VARCHAR(20)
    bool Active);               // BOOLEAN     NOT NULL

public sealed record XProductRow(
    string ProductCode,         // VARCHAR(20) PK
    string Description,         // VARCHAR(30) NOT NULL (source is 40)
    char ProductType,           // CHAR(1)     NOT NULL, 'C' | 'R'
    bool Repairable,            // BOOLEAN     NOT NULL
    string UnitCode,            // VARCHAR(4)  NOT NULL
    decimal? WeightKg,          // NUMERIC(12,3)
    string? HazmatClass,        // VARCHAR(4)
    string? DefaultPartner,     // VARCHAR(12) FK -> XPARTNER
    DateOnly? CreatedDate,      // DATE
    bool Active);               // BOOLEAN     NOT NULL

public sealed record XAlternateRow(
    string ProductCode,         // VARCHAR(20) PK, FK -> XPRODUCT
    string AlternateCode,       // VARCHAR(20) PK, FK -> XPRODUCT, <> ProductCode
    char AlternateType);        // CHAR(1)     NOT NULL

public sealed record XStockRow(
    string ProductCode,         // VARCHAR(20)   PK, FK -> XPRODUCT
    string LocationCode,        // VARCHAR(6)    NOT NULL, FK -> XLOCATION
    string? Bin,                // VARCHAR(10)
    decimal Quantity);          // NUMERIC(14,2) NOT NULL, >= 0

public sealed record XOrderRow(
    string OrderNumber,         // VARCHAR(16)   PK (derived from composite source key)
    string PartnerCode,         // VARCHAR(12)   NOT NULL, FK -> XPARTNER
    DateOnly OrderDate,         // DATE          NOT NULL
    char Status,                // CHAR(1)       NOT NULL, 'O' | 'C' | 'P'
    string Currency,            // CHAR(3)       NOT NULL
    decimal? Total,             // NUMERIC(14,2)
    string? Remarks);           // VARCHAR(200)

public sealed record XOrderLineRow(
    string OrderNumber,         // VARCHAR(16)   PK, FK -> XORDER
    int LineNo,                 // INTEGER       PK
    string ProductCode,         // VARCHAR(20)   NOT NULL, FK -> XPRODUCT
    decimal Quantity,           // NUMERIC(14,2) NOT NULL
    decimal Price,              // NUMERIC(14,2) NOT NULL
    DateOnly? DeliveryDate,     // DATE
    char Status);               // CHAR(1)       NOT NULL

// ---------------------------------------------------------------------------
// Schema limits, mirrored from the DDL so parse methods can check them
// ---------------------------------------------------------------------------

public static class Limits
{
    // Column lengths (PostgreSQL counts characters, so compare rune counts)
    public const int LocationCode = 6, LocationName = 40;
    public const int PartnerCode = 12, PartnerName = 60, AddressLine = 60, VatNo = 20;
    public const int ProductCode = 20, ProductDescription = 30, UnitCode = 4, HazmatClass = 4;
    public const int Bin = 10;
    public const int OrderNumber = 16, Remarks = 200;
    public const int CountryLength = 3, CurrencyLength = 3;

    // NUMERIC(p,s): max integer digits = p - s
    public const int WeightScale = 3, WeightIntDigits = 9;   // NUMERIC(12,3)
    public const int MoneyScale = 2, MoneyIntDigits = 12;    // NUMERIC(14,2)

    // CHECK constraints
    public static readonly IReadOnlySet<string> Currencies = new HashSet<string>(
        ["EUR", "GBP", "USD", "PLN", "CHF", "SEK", "NOK", "DKK"], StringComparer.Ordinal);
    public static readonly IReadOnlySet<char> ProductTypes = new HashSet<char>(['C', 'R']);
    public static readonly IReadOnlySet<char> OrderStatuses = new HashSet<char>(['O', 'C', 'P']);
}

// ---------------------------------------------------------------------------
// Source reference and the migration log
// ---------------------------------------------------------------------------

/// <summary>Where a row came from. SourceTable is the source file/table name,
/// Line is the 1-based data row (header excluded), Key is the source key as it
/// appears in the source (composite keys joined, e.g. "1001/A"). RawRow is the
/// complete, unmodified source row as text. It is only written out for skipped
/// and merged rows, so the log shows everything in the row, not just the field
/// that triggered the tag. It travels with every row because a row already in a
/// target table can still be skipped later (e.g. as a duplicate key).</summary>
public sealed record SourceRef(string SourceTable, int Line, string Key, string RawRow)
{
    public override string ToString() => $"{SourceTable}:{Line} [{Key}]";
}

public enum Outcome { Skipped, Merged }

/// <summary>One source row that did not become its own target row.
/// IntoKey is set for merges: the target primary key the row was folded into.</summary>
public sealed record LogEntry(
    Outcome Outcome, string TargetTable, SourceRef Source, string Tag, string Detail, string? IntoKey);

/// <summary>One line of the post-migration summary.</summary>
public sealed record TagSummary(string TargetTable, Outcome Outcome, string Tag, int Count);

/// <summary>The log of the whole run: every source row that did not become its own
/// row in the target table it was meant for. One instance, shared by all tables.</summary>
public sealed class MigrationLog
{
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries => _entries;

    /// <summary>Called by parse methods for rows that cannot be parsed or validated
    /// (pass the table's Name, e.g. tables.XProduct.Name), and by the target tables
    /// themselves for duplicate keys.</summary>
    public void AddSkipped(string targetTable, SourceRef source, string tag, string detail)
        => _entries.Add(new(Outcome.Skipped, targetTable, source, tag, detail, null));

    public void AddMerged(string targetTable, SourceRef source, string intoKey, string tag, string detail)
        => _entries.Add(new(Outcome.Merged, targetTable, source, tag, detail, intoKey));

    // Lookups. Each returns a finished list, computed at the moment of the call.

    public IReadOnlyList<LogEntry> ForSourceKey(string sourceTable, string key) => _entries
        .Where(e => SameText(e.Source.SourceTable, sourceTable) && SameText(e.Source.Key, key))
        .ToList();

    public IReadOnlyList<LogEntry> ForSourceTable(string sourceTable) => _entries
        .Where(e => SameText(e.Source.SourceTable, sourceTable))
        .ToList();

    public IReadOnlyList<LogEntry> ForTargetTable(string targetTable) => _entries
        .Where(e => SameText(e.TargetTable, targetTable))
        .ToList();

    private static bool SameText(string a, string b)
        => string.Equals(a, b, StringComparison.InvariantCultureIgnoreCase);

    /// <summary>A sorted copy of the log, for writing it to disk deterministically.
    /// The log itself is not reordered.</summary>
    public IReadOnlyList<LogEntry> Sorted() => _entries
        .OrderBy(e => e.Source.SourceTable, StringComparer.Ordinal)
        .ThenBy(e => e.Source.Line)
        .ThenBy(e => e.TargetTable, StringComparer.Ordinal)
        .ThenBy(e => e.Tag, StringComparer.Ordinal)
        .ToList();

    /// <summary>Post-migration summary: row counts per target table, outcome and tag,
    /// in deterministic order. Pass a table name to summarize just that table.</summary>
    public IReadOnlyList<TagSummary> SummaryByTag(string? targetTable = null)
    {
        bool summarizeAllTables = targetTable is null;
        var entries = summarizeAllTables ? _entries : ForTargetTable(targetTable!);

        return entries
            .GroupBy(e => (e.TargetTable, e.Outcome, e.Tag))
            .Select(g => new TagSummary(g.Key.TargetTable, g.Key.Outcome, g.Key.Tag, g.Count()))
            .OrderBy(s => s.TargetTable, StringComparer.Ordinal)
            .ThenBy(s => s.Outcome)
            .ThenBy(s => s.Tag, StringComparer.Ordinal)
            .ToList();
    }
}

// ---------------------------------------------------------------------------
// Target tables
// ---------------------------------------------------------------------------

public sealed record TableRow<T>(T Row, SourceRef Source);

/// <summary>Base for all target tables: rows stored by primary key, with the
/// building blocks for duplicate-key handling. Each table decides its own rule by
/// overriding ResolveDuplicate and calling one of the protected strategies. Rows
/// can only leave the table through methods that log them, so nothing disappears
/// silently. Rows are stored unordered; SortedRows is the only way to read them,
/// so output is always in primary key order.</summary>
public abstract class TargetTable<TKey, TRow>(string name, IKeyComparer<TKey> comparer, MigrationLog log)
    where TKey : notnull
{
    private readonly Dictionary<TKey, TableRow<TRow>> _rows = new(comparer);

    // All primary keys currently in the table, kept in step with _rows,
    // so other tables can check foreign keys against it.
    private readonly HashSet<TKey> _keys = new(comparer);

    // Keys rejected by SkipAll. Any later row with such a key is skipped too,
    // otherwise a third duplicate would slip in as if it were the only one.
    private readonly Dictionary<TKey, string> _rejectedKeys = new(comparer);

    private readonly IKeyComparer<TKey> _comparer = comparer;

    public string Name { get; } = name;
    protected MigrationLog Log { get; } = log;

    public int Count => _rows.Count;
    public bool Contains(TKey key) => _keys.Contains(key);
    public bool TryGet(TKey key, out TableRow<TRow> row) => _rows.TryGetValue(key, out row!);

    /// <summary>All rows in primary key order, computed at the moment of the call.</summary>
    public IReadOnlyList<TableRow<TRow>> SortedRows() => _rows
        .OrderBy(pair => pair.Key, _comparer)
        .Select(pair => pair.Value)
        .ToList();

    /// <summary>All primary keys currently in the table, for fast FK lookups from
    /// other tables. A live, read-only view: it reflects later inserts and removals.</summary>
    public IReadOnlySet<TKey> KeySet() => _keys;

    /// <summary>Where the primary key is in this table's row.</summary>
    protected abstract TKey KeyOf(TRow row);

    /// <summary>What this table does when a row arrives with a key that is already
    /// in the table. Implemented per table by calling one of the strategies below.</summary>
    protected abstract void ResolveDuplicate(TKey key, TableRow<TRow> existing, TableRow<TRow> incoming);

    public void Insert(TRow row, SourceRef source)
    {
        var key = KeyOf(row);
        var incoming = new TableRow<TRow>(row, source);

        bool keyWasRejectedEarlier = _rejectedKeys.ContainsKey(key);
        bool keyIsTaken = _keys.Contains(key);

        if (keyWasRejectedEarlier)
            SkipBecauseKeyWasRejected(key, incoming);
        else if (keyIsTaken)
            ResolveDuplicate(key, _rows[key], incoming);
        else
            AddRow(key, incoming);
    }

    // ----- Duplicate-key strategies, used from ResolveDuplicate ----------------

    /// <summary>Keep the row already in the table, skip the incoming one.</summary>
    protected void KeepFirst(TKey key, TableRow<TRow> existing, TableRow<TRow> incoming, string tag)
    {
        Log.AddSkipped(Name, incoming.Source, tag, $"Primary key {key} already taken by {existing.Source}");
    }

    /// <summary>Replace the row in the table with the incoming one, skip the replaced row.</summary>
    protected void KeepLast(TKey key, TableRow<TRow> existing, TableRow<TRow> incoming, string tag)
    {
        _rows[key] = incoming;
        Log.AddSkipped(Name, existing.Source, tag, $"Primary key {key} replaced by later row {incoming.Source}");
    }

    /// <summary>Skip every row with this key, including ones arriving later.</summary>
    protected void SkipAll(TKey key, TableRow<TRow> existing, TableRow<TRow> incoming, string tag)
    {
        RemoveRow(key);
        _rejectedKeys.Add(key, tag);
        Log.AddSkipped(Name, existing.Source, tag, DuplicateKeyDetail(key));
        Log.AddSkipped(Name, incoming.Source, tag, DuplicateKeyDetail(key));
    }

    // ----- Private helpers ------------------------------------------------------

    private void AddRow(TKey key, TableRow<TRow> row)
    {
        _rows.Add(key, row);
        _keys.Add(key);
    }

    private void RemoveRow(TKey key)
    {
        _rows.Remove(key);
        _keys.Remove(key);
    }

    private void SkipBecauseKeyWasRejected(TKey key, TableRow<TRow> incoming)
    {
        string tag = _rejectedKeys[key];
        Log.AddSkipped(Name, incoming.Source, tag, DuplicateKeyDetail(key));
    }

    private static string DuplicateKeyDetail(TKey key)
        => $"Primary key {key} occurs more than once in source; all occurrences skipped";
}

// ---------------------------------------------------------------------------
// Key comparers: case-insensitive, invariant culture
// ---------------------------------------------------------------------------

/// <summary>A key comparer does two jobs: equality and hashing for the
/// Dictionary/HashSet (is this the same key?), and ordering for SortedRows
/// (which key comes first?). Both must follow the same rules, so one object
/// provides both.</summary>
public interface IKeyComparer<TKey> : IEqualityComparer<TKey>, IComparer<TKey>;

/// <summary>Single code keys, e.g. LocationCode, ProductCode.</summary>
public sealed class CodeComparer : IKeyComparer<string>
{
    private static readonly StringComparer Text = StringComparer.InvariantCultureIgnoreCase;

    public bool Equals(string? a, string? b) => Text.Equals(a, b);
    public int GetHashCode(string key) => Text.GetHashCode(key);
    public int Compare(string? a, string? b) => Text.Compare(a, b);
}

/// <summary>Two-code keys, e.g. (ProductCode, AlternateCode).</summary>
public sealed class CodePairComparer : IKeyComparer<(string, string)>
{
    private static readonly StringComparer Text = StringComparer.InvariantCultureIgnoreCase;

    public bool Equals((string, string) a, (string, string) b)
        => Text.Equals(a.Item1, b.Item1) && Text.Equals(a.Item2, b.Item2);

    public int GetHashCode((string, string) key)
        => HashCode.Combine(Text.GetHashCode(key.Item1), Text.GetHashCode(key.Item2));

    public int Compare((string, string) a, (string, string) b)
    {
        int byFirstCode = Text.Compare(a.Item1, b.Item1);
        bool firstCodesDiffer = byFirstCode != 0;
        if (firstCodesDiffer)
            return byFirstCode;

        return Text.Compare(a.Item2, b.Item2);
    }
}

/// <summary>Code-and-number keys, e.g. (OrderNumber, LineNo).</summary>
public sealed class CodeAndNumberComparer : IKeyComparer<(string, int)>
{
    private static readonly StringComparer Text = StringComparer.InvariantCultureIgnoreCase;

    public bool Equals((string, int) a, (string, int) b)
        => Text.Equals(a.Item1, b.Item1) && a.Item2 == b.Item2;

    public int GetHashCode((string, int) key)
        => HashCode.Combine(Text.GetHashCode(key.Item1), key.Item2);

    public int Compare((string, int) a, (string, int) b)
    {
        int byCode = Text.Compare(a.Item1, b.Item1);
        bool codesDiffer = byCode != 0;
        if (codesDiffer)
            return byCode;

        return a.Item2.CompareTo(b.Item2);
    }
}

public static class KeyComparers
{
    public static readonly CodeComparer Code = new();
    public static readonly CodePairComparer CodePair = new();
    public static readonly CodeAndNumberComparer CodeAndNumber = new();
}

// ---------------------------------------------------------------------------
// One class per staging table: primary key + duplicate-key rule
// ---------------------------------------------------------------------------

public sealed class XLocationTable(MigrationLog log)
    : TargetTable<string, XLocationRow>("XLOCATION", KeyComparers.Code, log)
{
    protected override string KeyOf(XLocationRow row) => row.LocationCode;

    // DECISION PENDING: duplicate location codes. Until agreed, all occurrences are skipped.
    protected override void ResolveDuplicate(
        string key, TableRow<XLocationRow> existing, TableRow<XLocationRow> incoming)
        => SkipAll(key, existing, incoming, "XLOCATION_DUPLICATE_KEY");
}

public sealed class XPartnerTable(MigrationLog log)
    : TargetTable<string, XPartnerRow>("XPARTNER", KeyComparers.Code, log)
{
    protected override string KeyOf(XPartnerRow row) => row.PartnerCode;

    // DECISION PENDING: duplicate partner codes. Until agreed, all occurrences are skipped.
    protected override void ResolveDuplicate(
        string key, TableRow<XPartnerRow> existing, TableRow<XPartnerRow> incoming)
        => SkipAll(key, existing, incoming, "XPARTNER_DUPLICATE_KEY");
}

public sealed class XProductTable(MigrationLog log)
    : TargetTable<string, XProductRow>("XPRODUCT", KeyComparers.Code, log)
{
    protected override string KeyOf(XProductRow row) => row.ProductCode;

    // DECISION PENDING: duplicate product codes. Until agreed, all occurrences are skipped.
    protected override void ResolveDuplicate(
        string key, TableRow<XProductRow> existing, TableRow<XProductRow> incoming)
        => SkipAll(key, existing, incoming, "XPRODUCT_DUPLICATE_KEY");
}

public sealed class XAlternateTable(MigrationLog log)
    : TargetTable<(string, string), XAlternateRow>("XALTERNATE", KeyComparers.CodePair, log)
{
    protected override (string, string) KeyOf(XAlternateRow row) => (row.ProductCode, row.AlternateCode);

    // DECISION PENDING: duplicate (product, alternate) pairs. Until agreed, all occurrences are skipped.
    protected override void ResolveDuplicate(
        (string, string) key, TableRow<XAlternateRow> existing, TableRow<XAlternateRow> incoming)
        => SkipAll(key, existing, incoming, "XALTERNATE_DUPLICATE_KEY");
}

public sealed class XStockTable(MigrationLog log)
    : TargetTable<string, XStockRow>("XSTOCK", KeyComparers.Code, log)
{
    protected override string KeyOf(XStockRow row) => row.ProductCode;

    // DECISION PENDING: the source has many stock locations per product, the target
    // one. Needs a primary-location rule and a quantity rule (keep or sum).
    // Until agreed, all occurrences are skipped.
    protected override void ResolveDuplicate(
        string key, TableRow<XStockRow> existing, TableRow<XStockRow> incoming)
        => SkipAll(key, existing, incoming, "XSTOCK_DUPLICATE_KEY");
}

public sealed class XOrderTable(MigrationLog log)
    : TargetTable<string, XOrderRow>("XORDER", KeyComparers.Code, log)
{
    protected override string KeyOf(XOrderRow row) => row.OrderNumber;

    // A collision here means two different composite source keys produced the same
    // order number, i.e. the numbering scheme is wrong. Check source keys before
    // insert and throw on a true collision; SkipAll only covers exact source duplicates.
    // DECISION PENDING: duplicate orders in source. Until agreed, all occurrences are skipped.
    protected override void ResolveDuplicate(
        string key, TableRow<XOrderRow> existing, TableRow<XOrderRow> incoming)
        => SkipAll(key, existing, incoming, "XORDER_DUPLICATE_KEY");
}

public sealed class XOrderLineTable(MigrationLog log)
    : TargetTable<(string, int), XOrderLineRow>("XORDERLINE", KeyComparers.CodeAndNumber, log)
{
    protected override (string, int) KeyOf(XOrderLineRow row) => (row.OrderNumber, row.LineNo);

    // DECISION PENDING: duplicate line numbers within an order. Until agreed, all occurrences are skipped.
    protected override void ResolveDuplicate(
        (string, int) key, TableRow<XOrderLineRow> existing, TableRow<XOrderLineRow> incoming)
        => SkipAll(key, existing, incoming, "XORDERLINE_DUPLICATE_KEY");
}

// ---------------------------------------------------------------------------
// The staging "database"
// ---------------------------------------------------------------------------

/// <summary>All staging tables, sharing the run's single log.</summary>
public sealed class StagingTables
{
    public MigrationLog Log { get; } = new();

    public XLocationTable XLocation { get; }
    public XPartnerTable XPartner { get; }
    public XProductTable XProduct { get; }
    public XAlternateTable XAlternate { get; }
    public XStockTable XStock { get; }
    public XOrderTable XOrder { get; }
    public XOrderLineTable XOrderLine { get; }

    public StagingTables()
    {
        XLocation = new(Log);
        XPartner = new(Log);
        XProduct = new(Log);
        XAlternate = new(Log);
        XStock = new(Log);
        XOrder = new(Log);
        XOrderLine = new(Log);
    }
}