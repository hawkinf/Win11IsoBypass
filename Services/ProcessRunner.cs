using System.Diagnostics;
using System.Text;

namespace Win11IsoBypass.Services;

internal sealed class ProcessResult(int exitCode, string output)
{
    public int ExitCode { get; } = exitCode;
    public string Output { get; } = output;
}

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onOutput,
        CancellationToken cancellationToken,
        Func<int, bool>? success = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        void Capture(string? line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (output) output.AppendLine(line);
            onOutput?.Invoke(line);
        }

        if (!process.Start()) throw new InvalidOperationException($"Não foi possível iniciar {fileName}.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        var result = new ProcessResult(process.ExitCode, output.ToString());
        if (!(success ?? (code => code == 0))(result.ExitCode))
        {
            var details = result.Output.Length <= 4000 ? result.Output : result.Output[^4000..];
            throw new InvalidOperationException($"O comando {Path.GetFileName(fileName)} falhou (código {result.ExitCode}).\n{details}");
        }

        return result;
    }
}
