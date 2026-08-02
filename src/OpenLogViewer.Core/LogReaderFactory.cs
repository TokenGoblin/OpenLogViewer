namespace OpenLogViewer.Core;

/// <summary>Chooses a reader by sniffing file content, then decodes.</summary>
public static class LogReaderFactory
{
    // MaxxECU first: its logs are zips, and a zip offered to the delimited
    // reader is binary rubbish that it would refuse anyway — but sniffing for
    // the archive is cheaper and more certain than letting it try.
    private static readonly ILogReader[] Readers =
        [new MaxxLogReader(), new MlgReader(), new DelimitedLogReader()];

    public const string OpenFileFilter =
        "All datalogs|*.mlg;*.msl;*.csv;*.txt;*.log;*.tsv;*.dat;*.MaxxECU-Zip-log|" +
        "Binary log — MegaSquirt / rusEFI (*.mlg)|*.mlg|" +
        "TunerStudio text log (*.msl)|*.msl|" +
        "MaxxECU log (*.MaxxECU-Zip-log)|*.MaxxECU-Zip-log|" +
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
