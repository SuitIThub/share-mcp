using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Share.Core.Tests;

/// <summary>Runs the real share-server (built from ../server) on a free local port.</summary>
public sealed class GoServer : IDisposable
{
    private static readonly Lazy<string> Bin = new(Build);
    private readonly TempDir _dir = new();
    private readonly string _config;
    private readonly int _port;
    private Process? _process;

    public GoServer()
    {
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            _port = ((IPEndPoint)l.LocalEndpoint).Port;
        }
        Url = $"http://127.0.0.1:{_port}";
        _config = _dir.File("config.toml");
        File.WriteAllText(_config, $"listen = \"127.0.0.1:{_port}\"\ndb_path = \"{_dir.File("share.db").Replace('\\', '/')}\"\n");
        Start();
    }

    public string Url { get; }
    public static string ShareExe => Path.Combine(Bin.Value, "share.exe");
    private static string ServerExe => Path.Combine(Bin.Value, "share-server.exe");

    /// <summary>The tests talk to the real server and CLI; without Go they fail instead of silently skipping.</summary>
    private static string Build()
    {
        var windows = Path.GetFullPath(Path.Combine(SourceDir(), "..", ".."));
        var bin = Path.Combine(windows, "tests", "bin", "go");
        using var once = new Mutex(false, "share-tests-build-go");
        once.WaitOne();
        try
        {
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("GO_BIN") ?? "go")
            {
                WorkingDirectory = Path.Combine(windows, "..", "server"), RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                ArgumentList = { "build", "-o", bin + Path.DirectorySeparatorChar, "./cmd/share-server", "./cmd/share" },
                Environment = { ["CGO_ENABLED"] = "0" },
            };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("go build failed: " + err);
            return bin;
        }
        finally
        {
            once.ReleaseMutex();
        }
    }

    private static string SourceDir([CallerFilePath] string path = "") => Path.GetDirectoryName(path)!;

    private void Start()
    {
        _process = Process.Start(new ProcessStartInfo(ServerExe) { ArgumentList = { "-config", _config, "serve" }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        for (var end = DateTime.UtcNow.AddSeconds(15); ;)
        {
            try
            {
                using var c = new TcpClient("127.0.0.1", _port);
                return;
            }
            catch (SocketException)
            {
                if (_process.HasExited || DateTime.UtcNow > end) throw new InvalidOperationException("share-server did not start");
                Thread.Sleep(20);
            }
        }
    }

    public void Stop()
    {
        _process?.Kill();
        _process?.WaitForExit();
        _process?.Dispose();
        _process = null;
    }

    public void Restart()
    {
        Stop();
        Start();
    }

    public string PairCode()
    {
        using var p = Process.Start(new ProcessStartInfo(ServerExe) { ArgumentList = { "-config", _config, "pair" }, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return Regex.Match(o, @"Code:\s+(\S+)").Groups[1].Value;
    }

    public Task<Account> AccountAsync(string name) => Pairing.PairAsync(Url, PairCode(), name, Identity.Generate(name), allowInsecureLocal: true);

    public void Dispose()
    {
        Stop();
        _dir.Dispose();
    }
}
