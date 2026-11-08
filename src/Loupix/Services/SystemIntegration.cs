using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Loupix.Services;

/// <summary>
/// Ensures one running instance. Later launches (including loupix:// links) forward their
/// command line to the first instance over a named pipe and exit.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // string.GetHashCode() is randomized per process, so derive a stable per-user id instead.
    private static readonly string Id = "Loupix-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName)))[..16];
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }
    public event Action<string[]>? ArgumentsReceived;

    public SingleInstance()
    {
        _mutex = new Mutex(true, Id, out bool created);
        IsFirst = created;
    }

    public void StartListening()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Id, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(_cts.Token);
                    var args = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    ArgumentsReceived?.Invoke(args);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Error("Pipe listener", ex); await Task.Delay(500); }
            }
        });
    }

    public static bool SendToFirst(string[] args)
    {
        try
        {
            // We were launched by the user, so we may pass our right to take focus on to the running instance.
            AllowSetForegroundWindow(-1);
            using var client = new NamedPipeClientStream(".", Id, PipeDirection.Out);
            client.Connect(3000);
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', args.Length == 0 ? ["loupix://show"] : args));
            client.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    public void Dispose()
    {
        _cts.Cancel();
        if (IsFirst) _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

public static class SystemIntegration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Loupix.exe");

    /// <summary>Registers the loupix:// URL scheme for the current user (no admin rights needed).</summary>
    public static void RegisterUrlScheme()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\loupix");
            key.SetValue("", "URL:Loupix");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{ExePath}\",0");
            using var cmd = key.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", $"\"{ExePath}\" \"%1\"");
        }
        catch (Exception ex)
        {
            Log.Error("Could not register URL scheme", ex);
        }
    }

    /// <summary>Adds Loupix to the "Open with" list for images.</summary>
    public static void RegisterOpenWith()
    {
        try
        {
            using var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Loupix.exe");
            app.SetValue("FriendlyAppName", "Loupix");
            using (var cmd = app.CreateSubKey(@"shell\open\command")) cmd.SetValue("", $"\"{ExePath}\" \"%1\"");
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" })
            {
                types.SetValue(ext, "");
                using var owl = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithList\Loupix.exe");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not register Open With", ex);
        }
    }

    public static bool LaunchAtStartup
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("Loupix") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("Loupix", $"\"{ExePath}\" --background");
            else key.DeleteValue("Loupix", false);
        }
    }
}

/// <summary>Protects secrets (S3 secret key) with DPAPI for the current Windows user.</summary>
public static class SecretStore
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(Dpapi(data, protect: true));
    }

    public static string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        try
        {
            return Encoding.UTF8.GetString(Dpapi(Convert.FromBase64String(protectedText), protect: false));
        }
        catch (Exception ex)
        {
            Log.Error("Could not decrypt secret", ex);
            return "";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DATA_BLOB dataIn, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB dataIn, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, ref DATA_BLOB dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static byte[] Dpapi(byte[] input, bool protect)
    {
        var inBlob = new DATA_BLOB { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        var outBlob = new DATA_BLOB();
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            bool ok = protect
                ? CryptProtectData(ref inBlob, "Loupix", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, ref outBlob);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
