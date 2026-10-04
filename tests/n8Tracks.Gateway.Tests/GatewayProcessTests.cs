using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace n8Tracks.Gateway.Tests;

/// <summary>
/// Runs the built gateway as its own process, the way an operator does, and reads its exit code and
/// its real standard output.
/// </summary>
public sealed class GatewayProcessTests
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task AMissingApiUrlExitsWithCodeOneAndOneJsonLineNamingTheVariable()
    {
        var (exitCode, lines, _) = await RunToExit();

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        Assert.Equal("Error", line.GetProperty("LogLevel").GetString());
        Assert.Equal("N8TRACKS_API_URL", line.GetProperty("State").GetProperty("Variable").GetString());
        Assert.Contains("N8TRACKS_API_URL is required", line.GetProperty("Message").GetString(), StringComparison.Ordinal);
        Assert.True(line.GetProperty("Timestamp").TryGetDateTimeOffset(out _));
    }

    [Theory]
    [InlineData("N8TRACKS_GATEWAY_PORT", "70000")]
    [InlineData("N8TRACKS_GATEWAY_PORT", "http")]
    [InlineData("N8TRACKS_API_URL", "n8tracks:8787")]
    [InlineData("N8TRACKS_API_URL", "ftp://n8tracks")]
    [InlineData("N8TRACKS_LOG_LEVEL", "Chatty")]
    public async Task AnInvalidValueExitsWithCodeOneAndOneJsonLineNamingTheVariable(string variable, string value)
    {
        var (exitCode, lines, _) = await RunToExit(("N8TRACKS_API_URL", "http://127.0.0.1:1"), (variable, value));

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        Assert.Equal("Error", line.GetProperty("LogLevel").GetString());
        Assert.Equal(variable, line.GetProperty("State").GetProperty("Variable").GetString());
        Assert.Contains(variable, line.GetProperty("Message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFailureLineIsWrittenEvenAtTheQuietestLogLevelAndNeverEchoesTheUrl()
    {
        var (exitCode, lines, output) = await RunToExit(
            ("N8TRACKS_LOG_LEVEL", "Critical"),
            ("N8TRACKS_API_URL", "http://operator:hunter2@n8tracks:8787/?token=abc123"));

        Assert.Equal(1, exitCode);
        Assert.Equal("N8TRACKS_API_URL", Assert.Single(lines).GetProperty("State").GetProperty("Variable").GetString());
        Assert.DoesNotContain("hunter2", output, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APortInUseExitsWithCodeOneNamingThePortVariable()
    {
        // Dual-mode, any address: the same socket the gateway asks for.
        using var occupied = TcpListener.Create(0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;

        var (exitCode, lines, _) = await RunToExit(
            ("N8TRACKS_API_URL", "http://127.0.0.1:1"),
            ("N8TRACKS_GATEWAY_PORT", port.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(1, exitCode);
        var line = Assert.Single(lines);
        Assert.Equal("Error", line.GetProperty("LogLevel").GetString());
        Assert.Equal("N8TRACKS_GATEWAY_PORT", line.GetProperty("State").GetProperty("Variable").GetString());
        Assert.Contains($"port {port} is already in use", line.GetProperty("Message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheGatewayListensOnItsOwnPortOnlyAndLogsSingleLineJsonToStdout()
    {
        var port = GatewayNetworkTests.FreePort();
        var aspNetPort = GatewayNetworkTests.FreePort();
        var upstreamPort = GatewayNetworkTests.FreePort();

        using var gateway = Start(
            ("N8TRACKS_API_URL", $"http://127.0.0.1:{upstreamPort}"),
            ("N8TRACKS_GATEWAY_PORT", port.ToString(CultureInfo.InvariantCulture)),
            ("ASPNETCORE_URLS", $"http://127.0.0.1:{aspNetPort}"));
        try
        {
            using var client = new HttpClient();
            var body = await WaitForHealth(client, port);

            Assert.Equal("degraded", body.GetProperty("status").GetString());
            Assert.Equal("unreachable", body.GetProperty("upstream").GetString());
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri($"http://127.0.0.1:{aspNetPort}/health")));
        }
        finally
        {
            gateway.Process.Kill(entireProcessTree: true);
        }

        var (_, lines, output) = await gateway.Completion.WaitAsync(ExitTimeout);

        // Every line of standard output is one JSON object; nothing else is written.
        Assert.Equal(output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length, lines.Count);
        Assert.Contains(lines, line =>
            line.GetProperty("LogLevel").GetString() == "Information"
            && line.GetProperty("Category").GetString() == "n8Tracks.Gateway"
            && line.GetProperty("State").GetProperty("Port").GetInt32() == port);
        Assert.Contains(lines, line =>
            line.GetProperty("LogLevel").GetString() == "Warning"
            && line.GetProperty("Message").GetString()!.Contains("unreachable", StringComparison.Ordinal));
        Assert.DoesNotContain(upstreamPort.ToString(CultureInfo.InvariantCulture), output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheLogLevelSettingAppliesToStdout()
    {
        var port = GatewayNetworkTests.FreePort();

        using var gateway = Start(
            ("N8TRACKS_API_URL", $"http://127.0.0.1:{GatewayNetworkTests.FreePort()}"),
            ("N8TRACKS_GATEWAY_PORT", port.ToString(CultureInfo.InvariantCulture)),
            ("N8TRACKS_LOG_LEVEL", "error"));
        try
        {
            using var client = new HttpClient();
            await WaitForHealth(client, port);
        }
        finally
        {
            gateway.Process.Kill(entireProcessTree: true);
        }

        var (_, lines, _) = await gateway.Completion.WaitAsync(ExitTimeout);

        Assert.Empty(lines);
    }

    private static async Task<JsonElement> WaitForHealth(HttpClient client, int port)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/health"));
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return document.RootElement.Clone();
            }
            catch (HttpRequestException) when (deadline.Elapsed < ExitTimeout)
            {
                await Task.Delay(100);
            }
        }
    }

    private static async Task<(int ExitCode, IReadOnlyList<JsonElement> Lines, string Output)> RunToExit(params (string Name, string Value)[] variables)
    {
        using var gateway = Start(variables);
        try
        {
            return await gateway.Completion.WaitAsync(ExitTimeout);
        }
        catch (TimeoutException)
        {
            gateway.Process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static RunningGateway Start(params (string Name, string Value)[] variables)
    {
        var assembly = typeof(Program).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(assembly)!,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(assembly);

        // Nothing from the machine running the tests reaches the gateway's settings.
        foreach (var name in start.Environment.Keys
            .Where(name => name.StartsWith("N8TRACKS_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Logging__", StringComparison.OrdinalIgnoreCase))
            .ToList())
        {
            start.Environment.Remove(name);
        }

        foreach (var (name, value) in variables)
        {
            start.Environment[name] = value;
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("The gateway process did not start.");
        return new RunningGateway(process, Complete(process));
    }

    private static async Task<(int ExitCode, IReadOnlyList<JsonElement> Lines, string Output)> Complete(Process process)
    {
        var standardError = process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(string.Empty, await standardError);

        var lines = output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
                return document.RootElement.Clone();
            })
            .ToList();

        return (process.ExitCode, lines, output);
    }

    private sealed record RunningGateway(Process Process, Task<(int ExitCode, IReadOnlyList<JsonElement> Lines, string Output)> Completion) : IDisposable
    {
        public void Dispose()
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
            }

            Process.Dispose();
        }
    }
}
