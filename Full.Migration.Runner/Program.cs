// Program.cs
// Runs the XLOCATION mock migration from STWH.csv.
// Writes nothing to the console; all results go to files in the output folder:
//   migration_report.txt   reconciliation, summary by tag, then every log entry
//   migration_preview.txt  the first migrated rows
//
// Usage: dotnet run -- [source folder] [output folder]
//   defaults: <temp>/drills/data/stockman, <temp>/drills/migration_results

using System.Text;
using Migration.Load;
using Migration.Model;

// The user's temp folder is C:\Users\<user>\AppData\Local\Temp\ on Windows and
// /tmp/ on Linux, so the same code finds the source folder on both.
string sourceFolder = args.Length > 0
    ? args[0]
    : Path.Combine(Path.GetTempPath(), "drills", "data", "stockman");

string sourcePath = Path.Combine(sourceFolder, "STWH.csv");
string outputFolder = args.Length > 1
    ? args[1]
    : Path.Combine(Path.GetTempPath(), "drills", "migration_results","xLocation");

// Creates the folder (and any missing parent folders) if it doesn't exist yet,
// so the run also works from an empty machine. Does nothing if it already exists.
Directory.CreateDirectory(outputFolder);

string reportPath = Path.Combine(outputFolder, "migration_report.txt");
string previewPath = Path.Combine(outputFolder, "migration_preview.txt");

var tables = new StagingTables();

int sourceRows = XLocationMigration.Migrate(sourcePath, tables);

WriteReport(tables, sourceRows, reportPath);
WritePreview(tables, previewPath);

// ----- Output files ------------------------------------------------------------

// Reconciliation, summary by tag, then the full log. Each log entry is one
// header line with the searchable parts, then the raw source row exactly as it
// was in the file, then an empty line.
static void WriteReport(StagingTables tables, int sourceRows, string path)
{
    using var writer = CreateWriter(path);

    string targetTable = tables.XLocation.Name;
    int logged = tables.Log.ForTargetTable(targetTable).Count;

    writer.WriteLine("RECONCILIATION");
    writer.WriteLine($"{targetTable}: {sourceRows} source rows = {tables.XLocation.Count} migrated + {logged} skipped/merged");
    writer.WriteLine();

    writer.WriteLine("SUMMARY BY TAG");
    foreach (var line in tables.Log.SummaryByTag())
        writer.WriteLine($"{line.TargetTable,-12} {line.Outcome,-8} {line.Tag,-30} {line.Count,6}");
    writer.WriteLine();

    writer.WriteLine("LOG");
    foreach (var entry in tables.Log.Sorted())
    {
        writer.WriteLine($"{entry.TargetTable} | {entry.Outcome} | {entry.Tag} | {entry.Source} | {entry.Detail}");
        writer.WriteLine(entry.Source.RawRow);
        writer.WriteLine();
    }
}

static void WritePreview(StagingTables tables, string path)
{
    using var writer = CreateWriter(path);

    writer.WriteLine($"{tables.XLocation.Name}: first migrated rows");
    foreach (var tableRow in tables.XLocation.SortedRows().Take(20))
        writer.WriteLine($"{tableRow.Row.LocationCode,-6} | {tableRow.Row.Name,-40} | {tableRow.Row.Country}");
}

// Every output file is overwritten on each run and written as UTF-8 without
// BOM with "\n" line endings, so two runs on the same input produce
// byte-identical files on Windows and Linux.
static StreamWriter CreateWriter(string path)
{
    var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    writer.NewLine = "\n";
    return writer;
}