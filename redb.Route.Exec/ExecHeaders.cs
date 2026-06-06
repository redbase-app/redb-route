namespace redb.Route.Exec;

/// <summary>
/// Well-known header keys for exec exchanges.
/// </summary>
public static class ExecHeaders
{
    /// <summary>Common prefix for all exec component headers.</summary>
    public const string Prefix = "redbExec.";

    /// <summary>Inbound: command name to execute (overrides URI <c>command</c> when set).</summary>
    public const string Command = "redbExec.Command";

    /// <summary>Inbound: argument list (string[] or string parsed by simple whitespace split).</summary>
    public const string Args = "redbExec.Args";

    /// <summary>Outbound: process exit code (int).</summary>
    public const string ExitCode = "redbExec.ExitCode";

    /// <summary>Outbound: standard output (string).</summary>
    public const string Stdout = "redbExec.Stdout";

    /// <summary>Outbound: standard error (string).</summary>
    public const string Stderr = "redbExec.Stderr";

    /// <summary>Outbound: number of stdout bytes captured (long).</summary>
    public const string StdoutBytes = "redbExec.StdoutBytes";

    /// <summary>Outbound: number of stderr bytes captured (long).</summary>
    public const string StderrBytes = "redbExec.StderrBytes";

    /// <summary>Outbound: wall-clock duration of the process in milliseconds (long).</summary>
    public const string DurationMs = "redbExec.DurationMs";

    /// <summary>Outbound: <c>true</c> when the process was killed by the timeout watchdog.</summary>
    public const string TimedOut = "redbExec.TimedOut";

    /// <summary>Outbound: command line string actually launched (for audit/debugging).</summary>
    public const string CommandLine = "redbExec.CommandLine";
}
