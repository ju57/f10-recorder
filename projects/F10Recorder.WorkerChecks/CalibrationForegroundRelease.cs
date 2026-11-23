using System.Text.Json;

namespace F10Recorder;

// A tool can activate the fixture before its permission dialog has returned.
// Keep acquisition disarmed until the controller explicitly releases this
// fresh challenge after all UI calls. Actual foreground checks still apply.
internal sealed record CalibrationForegroundRelease(string Label, int Pid, long Hwnd, string Nonce)
{
    internal const string FileName = "foreground-handoff-release.json";
    internal static CalibrationForegroundRelease Create(string label, int pid, long hwnd)
        => new(label, pid, hwnd, Guid.NewGuid().ToString("N"));

    internal bool IsReleased(string root)
    {
        FileStream input;
        try { input = new(Path.Combine(root, FileName), FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return false; }
        using (input)
        {
            if (input.Length is <= 0 or > 4096) throw new IOException("Invalid foreground release size.");
            var bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            CalibrationForegroundRelease response;
            try { response = JsonSerializer.Deserialize<CalibrationForegroundRelease>(bytes); }
            catch (JsonException error) { throw new IOException("Invalid foreground release JSON.", error); }
            if (response != this) throw new IOException("Foreground release does not match this fixture and challenge.");
            return true;
        }
    }
}
