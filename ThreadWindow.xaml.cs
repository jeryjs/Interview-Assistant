using System.Text;
using System.Text.Json;
using Markdig;
using Naveen_Sir.Models;
using Naveen_Sir.Services;
using System.Windows;
using System.Windows.Media;

namespace Naveen_Sir;

public partial class ThreadWindow : Window
{
    private readonly MarkdownPipeline _markdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private readonly StringBuilder _reasoningBuilder = new();
    private readonly StringBuilder _answerBuilder = new();

    private bool _webViewReady;
    private bool _isDarkTheme;
    private bool _hasReasoning;
    private int _reasoningChars;
    private int _answerChars;
    private DateTime _lastRenderAt = DateTime.MinValue;

    public ThreadWindow()
    {
        InitializeComponent();
        _isDarkTheme = ThemeService.IsDarkTheme();
        ApplyTheme();
    }

    public async Task SetTopicAsync(string topic)
    {
        TopicTitleText.Text = topic;
        StatusText.Text = "Connecting…";

        _reasoningBuilder.Clear();
        _answerBuilder.Clear();
        _answerBuilder.Append($"# {topic}\n\n> Waiting for answer tokens…\n");

        _hasReasoning = false;
        SetReasoningVisible(false);
        _reasoningChars = 0;
        _answerChars = 0;
        _lastRenderAt = DateTime.MinValue;

        await RenderAsync(force: true);
    }

    public async Task AppendChunkAsync(TopicStreamChunk chunk)
    {
        if (chunk.Channel == TopicStreamChannel.Reasoning)
        {
            if (_reasoningChars == 0)
            {
                _reasoningBuilder.Clear();
                _hasReasoning = true;
                SetReasoningVisible(true);
            }

            _reasoningBuilder.Append(chunk.Text);
            _reasoningChars += chunk.Text.Length;
        }
        else
        {
            if (_answerChars == 0)
            {
                _answerBuilder.Clear();
            }

            _answerBuilder.Append(chunk.Text);
            _answerChars += chunk.Text.Length;
        }

        StatusText.Text = $"Streaming… reasoning {_reasoningChars} chars · answer {_answerChars} chars";
        await RenderAsync(force: false);
    }

    public async Task FlushAsync()
    {
        await RenderAsync(force: true);
        StatusText.Text = "Stream complete";
    }

    public async Task LoadFromCacheAsync(string cachePayload)
    {
        var cache = DeserializeCache(cachePayload);
        _reasoningBuilder.Clear();
        _answerBuilder.Clear();
        _reasoningBuilder.Append(cache.ReasoningMarkdown);
        _answerBuilder.Append(cache.AnswerMarkdown);
        _hasReasoning = !string.IsNullOrWhiteSpace(cache.ReasoningMarkdown);
        SetReasoningVisible(_hasReasoning);
        _reasoningChars = cache.ReasoningMarkdown.Length;
        _answerChars = cache.AnswerMarkdown.Length;
        StatusText.Text = "Loaded from cache";
        await RenderAsync(force: true);
    }

    public string GetCurrentCachePayload()
    {
        var payload = new ThreadCachePayload
        {
            ReasoningMarkdown = _reasoningBuilder.ToString(),
            AnswerMarkdown = _answerBuilder.ToString(),
        };

        return JsonSerializer.Serialize(payload);
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady)
        {
            return;
        }

        await ReasoningView.EnsureCoreWebView2Async();
        await AnswerView.EnsureCoreWebView2Async();

        ReasoningView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        ReasoningView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        AnswerView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        AnswerView.CoreWebView2.Settings.AreDevToolsEnabled = false;

        _webViewReady = true;
    }

    private async Task RenderAsync(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && (now - _lastRenderAt).TotalMilliseconds < 80)
        {
            return;
        }

        await EnsureWebViewAsync();
        var reasoningHtml = Markdown.ToHtml(_reasoningBuilder.ToString(), _markdownPipeline);
        var answerHtml = Markdown.ToHtml(_answerBuilder.ToString(), _markdownPipeline);

        ReasoningView.NavigateToString(BuildHtmlDocument(reasoningHtml));
        AnswerView.NavigateToString(BuildHtmlDocument(answerHtml));

        _lastRenderAt = now;
    }

    private string BuildHtmlDocument(string htmlBody)
    {
        var foreground = _isDarkTheme ? "#E6EEFF" : "#1D2A3D";
        var background = _isDarkTheme ? "#0D1420" : "#F8FBFF";
        var codeBackground = _isDarkTheme ? "#152235" : "#EAF2FF";
        var border = _isDarkTheme ? "#2F4260" : "#C9D9EE";

        return $@"<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"" />
    <style>
        body {{
            margin: 0;
            padding: 14px;
            color: {foreground};
            background: {background};
            font-family: Segoe UI, Inter, system-ui, sans-serif;
            line-height: 1.5;
            font-size: 13px;
        }}
        h1,h2,h3,h4 {{ margin-top: 18px; margin-bottom: 8px; }}
        pre, code {{
            background: {codeBackground};
            border: 1px solid {border};
            border-radius: 8px;
        }}
        code {{ padding: 2px 6px; }}
        pre {{ padding: 10px; overflow-x: auto; }}
        blockquote {{
            border-left: 3px solid {border};
            margin-left: 0;
            padding-left: 10px;
            opacity: 0.92;
        }}
        table {{ border-collapse: collapse; width: 100%; }}
        th, td {{ border: 1px solid {border}; padding: 6px; text-align: left; }}
        a {{ color: #72A6FF; }}
    </style>
</head>
<body>
{htmlBody}
</body>
</html>";
    }

    private void ApplyTheme()
    {
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
            _isDarkTheme ? "#111721" : "#F5F9FF"));
    }

    private void SetReasoningVisible(bool visible)
    {
        ReasoningSection.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ReasoningRow.Height = visible ? new GridLength(0.9, GridUnitType.Star) : new GridLength(0);
        AnswerRow.Height = visible ? new GridLength(1.6, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
    }

    private static ThreadCachePayload DeserializeCache(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return new ThreadCachePayload();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ThreadCachePayload>(payload);
            if (parsed is not null)
            {
                return parsed;
            }
        }
        catch
        {
            // old cache format fallback
        }

        return new ThreadCachePayload
        {
            AnswerMarkdown = payload,
            ReasoningMarkdown = string.Empty,
        };
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed class ThreadCachePayload
    {
        public string ReasoningMarkdown { get; set; } = string.Empty;
        public string AnswerMarkdown { get; set; } = string.Empty;
    }
}
