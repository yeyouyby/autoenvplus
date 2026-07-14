using AutoEnvPlus.Core.Discovery;
using AutoEnvPlus.Core.Runtimes;

namespace AutoEnvPlus.Core.Tests;

public sealed class RuntimeOutputParserTests
{
    [Theory]
    [InlineData(RuntimeKind.Python, "Python 3.13.5", "", "3.13.5")]
    [InlineData(RuntimeKind.NodeJs, "v22.17.0", "", "22.17.0")]
    [InlineData(RuntimeKind.Java, "", "openjdk version \"21.0.8\" 2025-07-15 LTS", "21.0.8")]
    [InlineData(RuntimeKind.Java, "", "java version \"1.8.0_452\"", "8.0.452")]
    [InlineData(RuntimeKind.DotNet, "10.0.200", "", "10.0.200")]
    [InlineData(RuntimeKind.CMake, "cmake version 4.1.0", "", "4.1.0")]
    [InlineData(RuntimeKind.Mingw, "gcc (x86_64-posix-seh, Built by MinGW-W64 project) 16.1.0", "", "16.1.0")]
    public void TryParse_RecognizesKnownOutputs(
        RuntimeKind kind,
        string output,
        string error,
        string expected)
    {
        bool success = RuntimeOutputParser.TryParse(kind, output, error, out RuntimeVersion? version);

        Assert.True(success);
        Assert.Equal(RuntimeVersion.Parse(expected), version);
    }

    [Fact]
    public void TryParse_RejectsUnrelatedText()
    {
        Assert.False(RuntimeOutputParser.TryParse(
            RuntimeKind.Python,
            "command not found",
            string.Empty,
            out _));
    }

    [Fact]
    public void DefaultProbes_IncludeGccAsMinGwToolchain()
    {
        RuntimeProbeDefinition probe = Assert.Single(
            RuntimeProbeDefinition.Defaults,
            candidate => candidate.Kind == RuntimeKind.Mingw);

        Assert.Equal("gcc", probe.Command);
        Assert.Equal(["--version"], probe.Arguments);
    }
}
