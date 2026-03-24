using System.ComponentModel;

namespace Naveen_Sir.Models;

public sealed class RecommendationEvent : INotifyPropertyChanged
{
    private DateTime _createdAt = DateTime.Now;
    private string _message = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "Event";
    public List<ChipItem> Chips { get; set; } = [];

    public DateTime CreatedAt
    {
        get => _createdAt;
        set
        {
            if (_createdAt == value)
            {
                return;
            }

            _createdAt = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CreatedAt)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeLabel)));
        }
    }

    public string Message
    {
        get => _message;
        set
        {
            if (_message == value)
            {
                return;
            }

            _message = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
        }
    }

    public bool IsTranscript => string.Equals(Kind, "Transcript", StringComparison.Ordinal);
    public string TimeLabel => CreatedAt.ToString("HH:mm:ss");
}