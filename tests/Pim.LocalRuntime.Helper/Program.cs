using System.Diagnostics;

namespace Pim.LocalRuntime.Helper;

/// <summary>
/// Deterministic synthetic runtime used by controlled-process tests. It performs no
/// inference and never downloads anything.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var mode = Value(args, "--mode", "ok");
        switch (mode)
        {
            case "ok":
                Console.Out.WriteLine("HELPER_OK");
                Console.Error.WriteLine("HELPER_STDERR");
                Console.Out.Flush();
                Console.Error.Flush();
                return 0;

            case "fail":
                Console.Error.WriteLine("HELPER_FAIL");
                Console.Error.Flush();
                return int.TryParse(Value(args, "--code", "3"), out var code) ? code : 3;

            case "sleep":
                Sleep(args, seconds: false);
                return 0;

            case "spawn-child":
                SpawnChild(args, exitImmediately: false);
                return 0;

            case "spawn-child-exit":
                SpawnChild(args, exitImmediately: true);
                return 0;

            case "flood":
                Flood(int.TryParse(Value(args, "--bytes", "200000"), out var bytes) ? bytes : 200000);
                return 0;

            case "whisper-json":
                return WhisperJson(args);

            default:
                Console.Error.WriteLine("HELPER_UNKNOWN_MODE");
                return 64;
        }
    }

    private static void Sleep(string[] args, bool seconds)
    {
        var value = int.TryParse(Value(args, "--ms", "60000"), out var ms) ? ms : 60000;
        Console.Out.WriteLine($"HELPER_PID {Environment.ProcessId}");
        Console.Out.Flush();
        Thread.Sleep(seconds ? value * 1000 : value);
    }

    private static void SpawnChild(string[] args, bool exitImmediately)
    {
        var ms = int.TryParse(Value(args, "--ms", "60000"), out var parsed) ? parsed : 60000;
        var executable = Environment.ProcessPath
                         ?? throw new InvalidOperationException("process path unavailable");
        var childInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        childInfo.ArgumentList.Add("--mode");
        childInfo.ArgumentList.Add("sleep");
        childInfo.ArgumentList.Add("--ms");
        childInfo.ArgumentList.Add(ms.ToString());

        using var child = Process.Start(childInfo)
                          ?? throw new InvalidOperationException("child did not start");
        var pidFile = Value(args, "--child-pid-file", string.Empty);
        if (!string.IsNullOrWhiteSpace(pidFile))
        {
            File.WriteAllText(pidFile, child.Id.ToString());
        }

        Console.Out.WriteLine($"HELPER_PID {Environment.ProcessId}");
        Console.Out.WriteLine($"CHILD_PID {child.Id}");
        Console.Out.Flush();

        if (!exitImmediately)
        {
            Thread.Sleep(ms);
        }
    }

    private static void Flood(int bytes)
    {
        var chunk = new string('A', 4096);
        var written = 0;
        while (written < bytes)
        {
            var take = Math.Min(chunk.Length, bytes - written);
            Console.Out.Write(chunk.AsSpan(0, take));
            written += take;
        }

        Console.Error.WriteLine("FLOOD_STDERR");
        Console.Out.Flush();
        Console.Error.Flush();
    }

    private static int WhisperJson(string[] args)
    {
        var modelPath = Value(args, "--model", string.Empty);
        var inputPath = Value(args, "--input", string.Empty);
        var language = Value(args, "--language", "auto");
        if (string.IsNullOrWhiteSpace(modelPath)
            || !File.Exists(modelPath)
            || string.IsNullOrWhiteSpace(inputPath)
            || !File.Exists(inputPath)
            || string.IsNullOrWhiteSpace(language))
        {
            Console.Error.WriteLine("HELPER_INPUT_INVALID");
            return 65;
        }

        Console.Out.WriteLine(
            "{\"transcription\":[{\"timestamps\":{\"from\":\"00:00:00.000\",\"to\":\"00:00:01.250\"}," +
            "\"offsets\":{\"from\":0,\"to\":1250},\"text\":\" synthetic transcript \"}]," +
            "\"text\":\"synthetic transcript\"}");
        Console.Out.Flush();
        return 0;
    }

    private static string Value(string[] args, string name, string fallback)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return fallback;
    }
}
