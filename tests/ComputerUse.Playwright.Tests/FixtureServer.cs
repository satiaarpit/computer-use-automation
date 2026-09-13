using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ComputerUse.Playwright.Tests;

public sealed class FixtureServer : IAsyncDisposable
{
    private Process? process;

    public Uri BaseAddress { get; private set; } = null!;

    public async Task StartAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        BaseAddress = new Uri($"http://127.0.0.1:{port}");

        var applicationAssembly = typeof(Program).Assembly.Location;
        process = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{applicationAssembly}\" --urls {BaseAddress}",
            WorkingDirectory = Path.GetDirectoryName(applicationAssembly),
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Could not start the deterministic fixture.");

        using var client = new HttpClient { BaseAddress = BaseAddress };
        var timeout = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < timeout)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The deterministic fixture exited before startup with code {process.ExitCode}.");
            }

            try
            {
                using var response = await client.GetAsync("/api/state/success");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The server has not bound its port yet.
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The deterministic fixture did not become ready within 15 seconds.");
    }

    public ValueTask DisposeAsync()
    {
        if (process is { HasExited: false })
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5_000);
        }

        process?.Dispose();
        return ValueTask.CompletedTask;
    }
}
