namespace OpenLogViewer.Core;

/// <summary>Chooses a reader by sniffing file content, then decodes.</summary>
public static class LogReaderFactory
{
    private static readonly ILogReader[] Readers = [new MlgReader(), new DelimitedLogReader()];

    public const string OpenFileFilter =
        "All datalogs|*.mlg;*.msl;*.csv;*.txt;*.log;*.tsv;*.dat|" +
        "Binary log — MegaSquirt / rusEFI (*.mlg)|*.mlg|" +
        "TunerStudio text log (*.msl)|*.msl|" +
        "Delimited text — CSV / TSV (*.csv;*.tsv;*.txt;*.log)|*.csv;*.tsv;*.txt;*.log|" +
        "All files|*.*";

    public static LogDocument Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Log file not found.", path);

        foreach (ILogReader reader in Readers)
            if (reader.CanRead(path))
                return reader.Read(path);

        throw new LogFormatException(
            $"'{Path.GetFileName(path)}' is not a recognised datalog format.");
    }
}
