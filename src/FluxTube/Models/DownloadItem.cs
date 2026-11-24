using System.ComponentModel;
using System.Runtime.CompilerServices;
using FluxTube.Services;

namespace FluxTube.Models;

public sealed class DownloadItem : INotifyPropertyChanged
{
    public long Id { get; set; }
    public string Url { get; set; } = "";
    public string? VideoId { get; set; }
    public string Quality { get; set; } = "";
    public string? File { get; set; }
    public string? ThumbFile { get; set; }
    public long SizeBytes { get; set; }
    public double DurationS { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public string? Error { get; set; }

    private string _title = "";
    public string Title { get => _title; set => Set(ref _title, value); }

    private string _channel = "";
    public string Channel { get => _channel; set => Set(ref _channel, value); }

    private string _status = "queued";
    public string Status
    {
        get => _status;
        set { if (Set(ref _status, value)) { On(nameof(StatusText)); On(nameof(IsActive)); On(nameof(IsDone)); } }
    }

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private string _speed = "";
    public string Speed { get => _speed; set { if (Set(ref _speed, value)) On(nameof(StatusText)); } }

    public bool IsActive => Status is "queued" or "downloading";
    public bool IsDone => Status == "done";

    public string Thumb => ThumbFile != null && System.IO.File.Exists(ThumbFile)
        ? new Uri(ThumbFile).AbsoluteUri
        : VideoId != null ? $"https://i.ytimg.com/vi/{VideoId}/mqdefault.jpg" : "https://i.ytimg.com/img/no_thumbnail.jpg";

    public string StatusText => Status switch
    {
        "queued" => "В очереди",
        "downloading" => $"{Progress:0.0}% · {Speed}",
        "done" => $"{Fmt.Size(SizeBytes)} · {Fmt.Time(DurationS)} · {Quality}",
        "error" => "Ошибка: " + Error,
        "canceled" => "Отменено",
        _ => Status,
    };

    public void Refresh() { On(nameof(StatusText)); On(nameof(Thumb)); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private bool Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return false;
        f = v; On(n); return true;
    }
}
