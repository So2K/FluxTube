using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluxTube.Models;

namespace FluxTube.Services;

/// <summary>yt-dlp download queue. Works for YouTube and the ~1800 other sites yt-dlp supports (TikTok, etc).</summary>
public sealed class Downloader
{
    public static Downloader Instance { get; } = new();

    public ObservableCollection<DownloadItem> Items { get; } = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DownloadItem> _doneById = new();
    private readonly Dictionary<long, Process> _running = new();
    private const int MaxParallel = 3;

    public void Init()
    {
        foreach (var d in Db.Downloads())
        {
            if (d.Status is "queued" or "downloading") { d.Status = "error"; d.Error = "прервано при закрытии"; }
            Items.Add(d);
            if (d.IsDone && !string.IsNullOrEmpty(d.VideoId) && d.File != null) _doneById[d.VideoId] = d;
        }
    }

    public void Shutdown()
    {
        lock (_running)
        {
            foreach (var p in _running.Values) try { p.Kill(true); } catch { }
            _running.Clear();
        }
    }

    public DownloadItem Enqueue(string url, string? title = null, string? channel = null, string? videoId = null, string? quality = null)
    {
        var d = new DownloadItem
        {
            Url = url, Title = title ?? url, Channel = channel ?? "", VideoId = videoId,
            Quality = quality ?? AppSettings.Current.DownloadQuality, Status = "queued",
        };
        d.Id = Db.AddDownload(d);
        Items.Insert(0, d);
        Pump();
        return d;
    }

    public void Retry(DownloadItem d)
    {
        d.Status = "queued"; d.Error = null; d.Progress = 0;
        Db.UpdateDownload(d);
        Pump();
    }

    public void Cancel(DownloadItem d)
    {
        lock (_running)
        {
            if (_running.TryGetValue(d.Id, out var p)) try { p.Kill(true); } catch { }
        }
        d.Status = "canceled";
        Db.UpdateDownload(d);
    }

    public void Remove(DownloadItem d, bool deleteFile)
    {
        if (d.IsActive) Cancel(d);
        if (deleteFile)
        {
            try { if (d.File != null && File.Exists(d.File)) File.Delete(d.File); } catch { }
            try { if (d.ThumbFile != null && File.Exists(d.ThumbFile)) File.Delete(d.ThumbFile); } catch { }
        }
        Db.DeleteDownload(d.Id);
        if (!string.IsNullOrEmpty(d.VideoId)) _doneById.TryRemove(d.VideoId, out _);
        Items.Remove(d);
    }

    public bool IsDownloaded(string videoId, out DownloadItem? item)
    {
        if (_doneById.TryGetValue(videoId, out var d) && d.IsDone && d.File != null && File.Exists(d.File))
        {
            item = d;
            return true;
        }
        item = null;
        return false;
    }

    private void Pump()
    {
        // by status, not by the running list: a download joins that list only once its task has started
        int active = Items.Count(i => i.Status == "downloading");
        foreach (var d in Items.Reverse().Where(i => i.Status == "queued").ToList())
        {
            if (active++ >= MaxParallel) break;
            d.Status = "downloading";
            _ = Task.Run(() => RunAsync(d));
        }
    }

    private static void Ui(Action a) => UiThread.Post(a);

    private async Task RunAsync(DownloadItem d)
    {
        var s = AppSettings.Current;
        var dir = s.DownloadDir;
        Directory.CreateDirectory(dir);
        var args = new List<string>
        {
            d.Url, "--no-playlist", "--no-colors", "--encoding", "utf-8", "--newline", "--progress",
            "-f", AppSettings.FormatFor(d.Quality),
            "-P", dir,
            "-o", "%(uploader|Unknown).80B/%(title).150B [%(id)s].%(ext)s",
            "-o", "thumbnail:.thumbs/%(id)s.%(ext)s",
            "--write-thumbnail", "--convert-thumbnails", "jpg",
            "--embed-metadata", "--embed-chapters", "--no-mtime", "-N", "4",
            "--progress-template", "download:FTP %(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.total_bytes,progress.total_bytes_estimate)s",
            "--print", "before_dl:FTI %(id)s|%(title)s|%(uploader)s|%(duration)s",
            "--print", "after_move:FTF %(filepath)s",
        };
        if (d.Quality == "audio") args.AddRange(["-x", "--audio-format", "m4a"]);
        else args.AddRange(["--merge-output-format", "mp4"]);
        if (s.SponsorBlockOnDownload) args.AddRange(["--sponsorblock-remove", string.Join(',', s.SponsorCategories)]);
        args.AddRange(YtDlp.CommonArgs(forPlayer: false));

        var psi = new ProcessStartInfo(Paths.YtDlp)
        {
            CreateNoWindow = true, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var errors = new StringBuilder();
        var errLock = new object();
        try
        {
            if (d.Status == "canceled") return;
            using var p = Process.Start(psi)!;
            lock (_running)
            {
                if (d.Status == "canceled") { try { p.Kill(true); } catch { } return; }
                _running[d.Id] = p;
            }
            p.ErrorDataReceived += (_, e) => { if (e.Data != null && e.Data.Contains("ERROR")) lock (errLock) errors.AppendLine(e.Data); };
            p.BeginErrorReadLine();
            string? line;
            while ((line = await p.StandardOutput.ReadLineAsync()) != null)
                Handle(d, line);
            await p.WaitForExitAsync();
            p.WaitForExit(); // wait for async ErrorDataReceived flush
            var ok = p.ExitCode == 0 && d.File != null;
            string errCopy;
            lock (errLock) errCopy = errors.ToString().Trim();
            Ui(() =>
            {
                if (d.Status == "canceled") return;
                if (ok)
                {
                    d.Status = "done";
                    d.Progress = 100;
                    try { d.SizeBytes = new FileInfo(d.File!).Length; } catch { }
                    if (d.VideoId != null)
                    {
                        var thumb = Path.Combine(dir, ".thumbs", d.VideoId + ".jpg");
                        if (File.Exists(thumb)) d.ThumbFile = thumb;
                        _doneById[d.VideoId] = d;
                    }
                }
                else
                {
                    d.Status = "error";
                    d.Error = errCopy.Length > 0 ? errCopy.Split('\n')[^1].Replace("ERROR: ", "") : $"код {p.ExitCode}";
                }
                d.Refresh();
                Db.UpdateDownload(d);
            });
        }
        catch (Exception e)
        {
            Ui(() => { d.Status = "error"; d.Error = e.Message; Db.UpdateDownload(d); });
        }
        finally
        {
            lock (_running) _running.Remove(d.Id);
            Ui(Pump);
        }
    }

    private void Handle(DownloadItem d, string line)
    {
        if (line.StartsWith("FTP "))
        {
            var f = line[4..].Split('|');
            if (f.Length < 4) return;
            double.TryParse(f[0].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct);
            var speed = f[1].Trim();
            var eta = f[2].Trim();
            Ui(() => { d.Progress = pct; d.Speed = $"{speed} · осталось {eta}"; });
        }
        else if (line.StartsWith("FTI "))
        {
            var f = line[4..].Split('|');
            if (f.Length < 4) return;
            double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var dur);
            Ui(() =>
            {
                d.VideoId = f[0]; d.Title = f[1]; d.Channel = f[2] == "NA" ? "" : f[2]; d.DurationS = dur;
                d.Refresh();
                Db.UpdateDownload(d);
            });
        }
        else if (line.StartsWith("FTF "))
        {
            d.File = line[4..].Trim();
        }
    }
}
