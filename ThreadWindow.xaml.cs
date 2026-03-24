using System.Text;
using Markdig;
using Naveen_Sir.Services;
using System.Windows;
using System.Windows.Media;

namespace Naveen_Sir;

public partial class ThreadWindow : Window
{
    private readonly MarkdownPipeline _markdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    private readonly StringBuilder _markdownBuilder = new();

    private bool _webViewReady;
    private bool _isDarkTheme;

    public ThreadWindow()
    {
        InitializeComponent();
        _isDarkTheme = ThemeService.IsDarkTheme();
        ApplyTheme();
    }

    public async Task SetTopicAsync(string topic)
    {
        TopicTitleText.Text = topic;
        StatusText.Text = "Generating...";
        _markdownBuilder.Clear();
        await RenderAsync();
    }

    public async Task LoadMarkdownAsync(string markdown)
    {
        _markdownBuilder.Clear();
        _markdownBuilder.Append(markdown);
        StatusText.Text = "Loaded from cache";
        await RenderAsync();
    }

    public async Task AppendMarkdownAsync(string chunk)
    {
        _markdownBuilder.Append(chunk);
        StatusText.Text = $"Streaming {DateTime.Now:HH:mm:ss}";
        await RenderAsync();
    }

    public string GetCurrentMarkdown()
    {
        return _markdownBuilder.ToString();
    }

    private async Task EnsureWebViewAsync()
    {
        if (_webViewReady)
        {
            return;
        }

        await MarkdownView.EnsureCoreWebView2Async();
        MarkdownView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        MarkdownView.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _webViewReady = true;
    }

    private async Task RenderAsync()
    {
        await EnsureWebViewAsync();
        var markdown = _markdownBuilder.ToString();
        var htmlBody = Markdown.ToHtml(markdown, _markdownPipeline);
        MarkdownView.NavigateToString(BuildHtmlDocument(htmlBody));
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
            padding: 26px;
            color: {foreground};
            background: {background};
            font-family: Segoe UI, Inter, system-ui, sans-serif;
            line-height: 1.6;
            font-size: 14px;
        }}
        h1,h2,h3,h4 {{ margin-top: 22px; margin-bottom: 10px; }}
        pre, code {{
            background: {codeBackground};
            border: 1px solid {border};
            border-radius: 8px;
        }}
        code {{ padding: 2px 6px; }}
        pre {{ padding: 12px; overflow-x: auto; }}
        blockquote {{
            border-left: 3px solid {border};
            margin-left: 0;
            padding-left: 12px;
            opacity: 0.92;
        }}
        table {{ border-collapse: collapse; width: 100%; }}
        th, td {{ border: 1px solid {border}; padding: 8px; text-align: left; }}
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
        if (_isDarkTheme)
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#111721"));
        }
        else
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F5F9FF"));
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}