#requires -Version 5
<#
.SYNOPSIS
    Delete every .dat file on a drive that does NOT live under a folder
    literally named "master". Tuned for high file counts: enumeration is
    a single pass that buckets by parent directory, then a worker pool
    deletes each bucket end-to-end so threads never contend on the same
    NTFS directory index.

.PARAMETER Root
    Drive / folder to scan. Defaults to Z:\.

.PARAMETER Force
    Actually delete (permanent — does not use the Recycle Bin). Without
    this switch the script is a dry-run.

.PARAMETER Threads
    Worker count for the delete phase. Default 32. NTFS only allows so
    much real parallelism on a single drive; 16–64 is the useful range.

.EXAMPLE
    .\cleanup_dat.ps1                       # dry run
    .\cleanup_dat.ps1 -Force                 # delete, 32 workers
    .\cleanup_dat.ps1 -Force -Threads 64     # NVMe / lots of dirs
#>
[CmdletBinding()]
param(
    [string]$Root    = 'Z:\',
    [switch]$Force,
    [int]   $Threads = 32
)

if (-not (Test-Path -LiteralPath $Root)) {
    Write-Error "Root not found: $Root"
    exit 1
}

Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public static class FastDatCleanup
{
    public sealed class Result
    {
        public long Considered;          // .dat files seen during enumeration
        public long Kept;                // skipped because under \master\
        public long Deleted;             // successfully removed
        public long Failed;              // delete attempted but threw
        public long Bytes;               // sum of (would-)deleted sizes
        public double EnumerateSeconds;
        public double DeleteSeconds;
        public ConcurrentBag<string> Errors = new ConcurrentBag<string>();
    }

    public static Result Run(string root, bool force, int threads, bool dryRunVerbose)
    {
        var result = new Result();
        var sep    = Path.DirectorySeparatorChar.ToString();
        var skip   = sep + "master" + sep;

        var enumOpts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible    = true,
            AttributesToSkip      = FileAttributes.ReparsePoint,  // include hidden+system, skip symlinks
        };

        // ── Phase 1: enumerate and bucket by directory ───────────────────
        //
        // Single-threaded enumeration is fastest here: Directory.EnumerateFiles
        // is FindFirstFile-backed and stays in one MFT region at a time.
        // We collect candidate paths into per-directory lists so the delete
        // phase can hand each worker a whole directory's worth at once —
        // that's what eliminates contention on the directory's $INDEX_ALLOCATION.
        var byDir = new Dictionary<string, List<string>>(8192, StringComparer.OrdinalIgnoreCase);

        var swE = Stopwatch.StartNew();
        foreach (var path in Directory.EnumerateFiles(root, "*.dat", enumOpts))
        {
            result.Considered++;
            if (path.IndexOf(skip, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                result.Kept++;
                continue;
            }
            var dir = Path.GetDirectoryName(path) ?? "";
            if (!byDir.TryGetValue(dir, out var bucket))
            {
                bucket = new List<string>(8);
                byDir[dir] = bucket;
            }
            bucket.Add(path);
        }
        swE.Stop();
        result.EnumerateSeconds = swE.Elapsed.TotalSeconds;

        // Size pass — best-effort, single thread, still fast at 80k entries.
        // We do it before the parallel delete so the size counter is final
        // and consistent in both dry-run and force modes.
        foreach (var bucket in byDir.Values)
        {
            foreach (var p in bucket)
            {
                try { result.Bytes += new FileInfo(p).Length; } catch { }
            }
        }

        if (!force)
        {
            if (dryRunVerbose)
            {
                foreach (var bucket in byDir.Values)
                    foreach (var p in bucket) Console.WriteLine("would delete: " + p);
            }
            return result;
        }

        // ── Phase 2: delete by directory bucket ──────────────────────────
        //
        // Workers each grab a whole directory's bucket and chew through it
        // sequentially. Threads never collide on the same parent dir, which
        // is where NTFS would otherwise serialize them.
        var po = new ParallelOptions { MaxDegreeOfParallelism = threads };

        var swD = Stopwatch.StartNew();
        Parallel.ForEach(byDir, po, kv =>
        {
            foreach (var path in kv.Value)
            {
                try
                {
                    // Clear read-only / hidden / system bits if set.
                    try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
                    File.Delete(path);
                    Interlocked.Increment(ref result.Deleted);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref result.Failed);
                    result.Errors.Add(path + "  --  " + ex.Message);
                }
            }
        });
        swD.Stop();
        result.DeleteSeconds = swD.Elapsed.TotalSeconds;

        return result;
    }
}
'@ -Language CSharp

$r = [FastDatCleanup]::Run($Root, [bool]$Force, [int]$Threads, $true)

$mb = [math]::Round($r.Bytes / 1MB, 1)

Write-Host ""
if ($Force) {
    $total = $r.EnumerateSeconds + $r.DeleteSeconds
    $rate  = if ($total -gt 0) { [int]($r.Deleted / $total) } else { 0 }
    Write-Host ("Deleted {0} file(s), {1} MB. Kept {2} under master\. Failed: {3}." -f $r.Deleted, $mb, $r.Kept, $r.Failed)
    Write-Host ("Enumerate {0:N2}s  |  Delete {1:N2}s  |  {2} files/sec  |  {3} threads" -f $r.EnumerateSeconds, $r.DeleteSeconds, $rate, $Threads)
} else {
    $would = $r.Considered - $r.Kept
    Write-Host ("Would delete {0} file(s), {1} MB. Kept {2} under master\." -f $would, $mb, $r.Kept)
    Write-Host ("Enumerate {0:N2}s  (delete phase skipped)" -f $r.EnumerateSeconds)
    if ($would -gt 0) { Write-Host "Re-run with -Force to actually delete." }
}

if ($r.Failed -gt 0) {
    Write-Host ""
    Write-Host "Errors (first 10):" -ForegroundColor Yellow
    $r.Errors | Select-Object -First 10 | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
}
