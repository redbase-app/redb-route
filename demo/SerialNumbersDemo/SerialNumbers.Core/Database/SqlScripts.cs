namespace SerialNumbers.Core.Database;

/// <summary>SQL scripts compiled into the module as resources.</summary>
public static class SqlScripts
{
    /// <summary>Creates the flat tables when they are missing. Safe to run on every start.</summary>
    public static readonly string Schema = Load("schema.sql");

    private static string Load(string fileName)
    {
        var name = $"{typeof(SqlScripts).Namespace}.{fileName}";
        using var stream = typeof(SqlScripts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded SQL script '{name}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
