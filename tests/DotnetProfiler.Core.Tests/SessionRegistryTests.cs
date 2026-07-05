using DotnetProfiler.Core;
using DotnetProfiler.Core.Collection;
using DotnetProfiler.Core.Sessions;

namespace DotnetProfiler.Core.Tests;

public class SessionRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dpmcp-reg-{Guid.NewGuid():N}");
    private readonly SessionRegistry _registry;

    public SessionRegistryTests() => _registry = new SessionRegistry(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Create_ThenResolveById()
    {
        var session = _registry.Create("cpu");
        Assert.StartsWith("cpu-", session.Id);
        Assert.Equal(session.TraceFilePath, _registry.ResolveTracePath(session.Id));
    }

    [Fact]
    public void Resolve_LiteralPath_AndPriorRunArtifact()
    {
        var literal = Path.Combine(_dir, "external.nettrace");
        File.WriteAllBytes(literal, [1]);
        Assert.Equal(literal, _registry.ResolveTracePath(literal));

        // Same file layout, fresh registry: id resolution must survive a server restart.
        File.WriteAllBytes(Path.Combine(_dir, "cpu-old-id.nettrace"), [1]);
        var fresh = new SessionRegistry(_dir);
        Assert.Equal(Path.Combine(_dir, "cpu-old-id.nettrace"), fresh.ResolveTracePath("cpu-old-id"));
    }

    [Fact]
    public void Resolve_Unknown_ThrowsSessionNotFound()
    {
        var ex = Assert.Throws<ProfilingException>(() => _registry.ResolveTracePath("nope"));
        Assert.Equal(ProfilingException.Codes.SessionNotFound, ex.Code);
    }
}

public class CommandLineTests
{
    [Theory]
    [InlineData("dotnet app.dll 5", "dotnet", "app.dll 5")]
    [InlineData("\"/path with space/dotnet\" \"app.dll\" 5", "/path with space/dotnet", "\"app.dll\" 5")]
    [InlineData("/usr/bin/true", "/usr/bin/true", "")]
    public void Split_HandlesQuotesAndArgs(string input, string file, string args)
    {
        var (fileName, arguments) = CommandLine.Split(input);
        Assert.Equal(file, fileName);
        Assert.Equal(args, arguments);
    }

    [Fact]
    public void Split_Empty_Throws()
    {
        var ex = Assert.Throws<ProfilingException>(() => CommandLine.Split("  "));
        Assert.Equal(ProfilingException.Codes.InvalidArguments, ex.Code);
    }
}
