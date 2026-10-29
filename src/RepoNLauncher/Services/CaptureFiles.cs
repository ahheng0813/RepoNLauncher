using System.IO;

namespace RepoNLauncher.Services;

public enum CaptureKind
{
    Screenshot,
    CameraPhoto,
    ScreenRecording,
    CameraRecording,
    VoiceRecording,
}

/// <summary>Where the Phone screen saves what it captures: a Repo'N'Launcher folder in Pictures, Videos or Music.</summary>
public static class CaptureFiles
{
    /// <summary>A new file for a capture, e.g. Pictures\Repo'N'Launcher\Screenshot Pixel 8 2026-10-29 14.03.22.png.</summary>
    public static string NewPath(CaptureKind kind, string deviceName)
    {
        var (library, label, extension) = kind switch
        {
            CaptureKind.Screenshot => (Environment.SpecialFolder.MyPictures, "Screenshot", ".png"),
            CaptureKind.CameraPhoto => (Environment.SpecialFolder.MyPictures, "Camera photo", ".png"),
            CaptureKind.ScreenRecording => (Environment.SpecialFolder.MyVideos, "Screen recording", ".mp4"),
            CaptureKind.CameraRecording => (Environment.SpecialFolder.MyVideos, "Camera recording", ".mp4"),
            _ => (Environment.SpecialFolder.MyMusic, "Voice recording", ".m4a"),
        };

        var folder = Path.Combine(Environment.GetFolderPath(library), "Repo'N'Launcher");
        Directory.CreateDirectory(folder);

        var invalid = Path.GetInvalidFileNameChars();
        var name = string.Concat($"{label} {deviceName} {DateTime.Now:yyyy-MM-dd HH.mm.ss}".Select(c => invalid.Contains(c) ? '_' : c));
        var path = Path.Combine(folder, name + extension);
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{name} ({n}){extension}");
        return path;
    }
}
