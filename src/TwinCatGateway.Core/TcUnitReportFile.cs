using System;
using System.IO;
using System.Security.Cryptography;

namespace TwinCatGateway.Core;

public sealed class TcUnitReportBaseline
{
    public string Path { get; set; } = string.Empty;

    public bool Exists { get; set; }

    public long Length { get; set; }

    public DateTimeOffset? LastWriteTimeUtc { get; set; }

    public string? Sha256 { get; set; }

    public bool ExistingReportDeleted { get; set; }
}

public sealed class TcUnitReportSnapshot
{
    public string Path { get; set; } = string.Empty;

    public bool Exists { get; set; }

    public long Length { get; set; }

    public DateTimeOffset? LastWriteTimeUtc { get; set; }

    public string? Sha256 { get; set; }
}

public static class TcUnitReportFile
{
    public static TcUnitReportBaseline CaptureBaseline(
        string path,
        bool allowDeleteExistingReport)
    {
        TcUnitReportSnapshot snapshot = Capture(path);
        bool deleted = false;
        if (snapshot.Exists
            && allowDeleteExistingReport)
        {
            File.Delete(snapshot.Path);
            deleted = true;
        }

        return new TcUnitReportBaseline
        {
            Path = snapshot.Path,
            Exists = snapshot.Exists,
            Length = snapshot.Length,
            LastWriteTimeUtc =
                snapshot.LastWriteTimeUtc,
            Sha256 = snapshot.Sha256,
            ExistingReportDeleted = deleted,
        };
    }

    public static TcUnitReportSnapshot Capture(
        string path)
    {
        string fullPath = ResolvePath(path);
        if (!File.Exists(fullPath))
        {
            return new TcUnitReportSnapshot
            {
                Path = fullPath,
            };
        }

        try
        {
            FileInfo file = new(fullPath);
            long length = file.Length;
            DateTimeOffset lastWriteTimeUtc =
                file.LastWriteTimeUtc;
            string hash;
            using (
                FileStream stream = new(
                    fullPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite
                        | FileShare.Delete))
            using (SHA256 sha256 = SHA256.Create())
            {
                hash = Convert.ToBase64String(
                    sha256.ComputeHash(stream));
            }

            return new TcUnitReportSnapshot
            {
                Path = fullPath,
                Exists = true,
                Length = length,
                LastWriteTimeUtc =
                    lastWriteTimeUtc,
                Sha256 = hash,
            };
        }
        catch (FileNotFoundException)
        {
            return new TcUnitReportSnapshot
            {
                Path = fullPath,
            };
        }
    }

    public static bool IsFresh(
        TcUnitReportBaseline baseline,
        TcUnitReportSnapshot current)
    {
        if (baseline is null)
        {
            throw new ArgumentNullException(
                nameof(baseline));
        }

        if (current is null)
        {
            throw new ArgumentNullException(
                nameof(current));
        }

        if (!string.Equals(
            baseline.Path,
            current.Path,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Report baseline and snapshot paths differ.",
                nameof(current));
        }

        if (!current.Exists)
        {
            return false;
        }

        if (!baseline.Exists
            || baseline.ExistingReportDeleted)
        {
            return true;
        }

        return baseline.Length != current.Length
            || baseline.LastWriteTimeUtc
                != current.LastWriteTimeUtc
            || !string.Equals(
                baseline.Sha256,
                current.Sha256,
                StringComparison.Ordinal);
    }

    public static bool HasSameFileState(
        TcUnitReportSnapshot first,
        TcUnitReportSnapshot second)
    {
        if (first is null)
        {
            throw new ArgumentNullException(
                nameof(first));
        }

        if (second is null)
        {
            throw new ArgumentNullException(
                nameof(second));
        }

        return string.Equals(
                first.Path,
                second.Path,
                StringComparison.OrdinalIgnoreCase)
            && first.Exists == second.Exists
            && first.Length == second.Length
            && first.LastWriteTimeUtc
                == second.LastWriteTimeUtc
            && string.Equals(
                first.Sha256,
                second.Sha256,
                StringComparison.Ordinal);
    }

    public static string ReadAllText(
        TcUnitReportSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(
                nameof(snapshot));
        }

        if (!snapshot.Exists)
        {
            throw new FileNotFoundException(
                "The TcUnit report does not exist.",
                snapshot.Path);
        }

        using StreamReader reader = new(
            snapshot.Path,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException(
                "TcUnit report path is required.",
                nameof(path));
        }

        if (!Path.IsPathRooted(path))
        {
            throw new ArgumentException(
                "TcUnit report path must be absolute.",
                nameof(path));
        }

        string fullPath = Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            throw new ArgumentException(
                "TcUnit report path identifies a directory.",
                nameof(path));
        }

        return fullPath;
    }
}
