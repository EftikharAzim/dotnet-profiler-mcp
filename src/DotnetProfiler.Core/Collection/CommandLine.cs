namespace DotnetProfiler.Core.Collection;

internal static class CommandLine
{
    /// <summary>Splits a command line into executable + arguments, honoring double quotes.</summary>
    public static (string FileName, string Arguments) Split(string commandLine)
    {
        string trimmed = commandLine.Trim();
        if (trimmed.Length == 0)
            throw new ProfilingException(ProfilingException.Codes.InvalidArguments, "launchCommand is empty.");

        if (trimmed[0] == '"')
        {
            int closing = trimmed.IndexOf('"', 1);
            if (closing < 0)
                throw new ProfilingException(ProfilingException.Codes.InvalidArguments, "Unbalanced quote in launchCommand.");
            return (trimmed[1..closing], trimmed[(closing + 1)..].TrimStart());
        }

        int space = trimmed.IndexOf(' ');
        return space < 0 ? (trimmed, "") : (trimmed[..space], trimmed[(space + 1)..].TrimStart());
    }
}
