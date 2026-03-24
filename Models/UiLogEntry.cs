using System.ComponentModel;

namespace Naveen_Sir.Models;

public sealed class UiLogEntry : INotifyPropertyChanged
{
    private bool _isExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public DateTime Timestamp { get; } = DateTime.Now;
    public string RawMessage { get; }
    public string Summary { get; }

    public UiLogEntry(string message)
    {
        RawMessage = message.Trim();
        Summary = BuildSummary(RawMessage);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayText)));
        }
    }

    public string DisplayText
    {
        get
        {
            var body = IsExpanded ? RawMessage : Summary;
            return $"[{Timestamp:HH:mm:ss}] {body}";
        }
    }

    private static string BuildSummary(string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (singleLine.Length <= 120)
        {
            return singleLine;
        }

        return singleLine[..120] + "…";
    }
}