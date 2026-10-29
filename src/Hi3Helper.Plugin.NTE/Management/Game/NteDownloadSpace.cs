using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Hi3Helper.Plugin.NTE.Management.Game;

/// <summary>Already allocated bytes and remaining network/disk work are different quantities.</summary>
internal sealed record NteDownloadSpace(long TargetBytes, long TemporaryBytes, long ExistingBytes, long ResumableBytes)
{
    internal long NetworkBytes => TargetBytes - ResumableBytes;
    internal long AdditionalBytes => Math.Max(0, TargetBytes - TemporaryBytes);
    internal long ReclaimedBytes => ExistingBytes + Math.Max(0, TemporaryBytes - TargetBytes);

    internal static NteDownloadSpace Capture(NteCNGameInstaller.DownloadTask task)
    {
        long temporary = Length(task.OutputPath + ".tmp");
        long resumable = 0;
        string identity = task.OutputPath + ".tmp.identity";
        if (temporary <= task.TotalSize && File.Exists(identity) &&
            File.ReadAllText(identity) == task.ExpectedMd5 + ":" + task.TotalSize.ToString(CultureInfo.InvariantCulture))
            resumable = temporary;
        return new(task.TotalSize, temporary, Length(task.OutputPath), resumable);
    }

    private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    // Completing a replacement releases its old file only after the new file is verified.
    // Gain tasks run by ascending cost; loss tasks by descending return. This avoids
    // rejecting a feasible update just because a space-releasing job was listed last.
    internal static long RequiredFreeBytes(IEnumerable<NteDownloadSpace> files, long reserve)
    {
        long balance = 0, required = 0;
        foreach (NteDownloadSpace file in files
            .OrderByDescending(f => f.ReclaimedBytes >= f.AdditionalBytes)
            .ThenBy(f => f.ReclaimedBytes >= f.AdditionalBytes ? f.AdditionalBytes : -f.ReclaimedBytes))
        {
            if (file.AdditionalBytes > 0)
                required = Math.Max(required, checked(file.AdditionalBytes + reserve - balance));
            balance = checked(balance + file.ReclaimedBytes - file.AdditionalBytes);
        }
        return Math.Max(0, required);
    }
}
