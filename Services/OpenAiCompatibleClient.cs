using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Naveen_Sir.Models;

namespace Naveen_Sir.Services;

public sealed class OpenAiCompatibleClient
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(35),
    };

    public async IAsyncEnumerable<string> StreamRecommendationTextAsync(
        ProviderLoadout loadout,
        string transcriptContext,
        IReadOnlyList<FrameSnapshot> keyframes,
        IReadOnlyList<string> existingChips,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var systemPrompt = BuildRecommendationSystemPrompt();
        var userText = BuildRecommendationUserPrompt(transcriptContext, keyframes, existingChips);
        var userContent = BuildUserContent(userText, keyframes);

        var payload = new
        {
            model = loadout.ModelId,
            stream = true,
            temperature = loadout.Temperature,
            max_tokens = Math.Max(200, loadout.MaxTokens),
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = systemPrompt,
                },
                new
                {
                    role = "user",
                    content = userContent,
                },
            },
        };

        await foreach (var chunk in SendStreamingChatAsync(loadout, payload, cancellationToken))
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<string> StreamTopicMarkdownAsync(
        ProviderLoadout loadout,
        string topic,
        string transcriptContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var systemPrompt = "You are an elite interview copilot. Output clean markdown only, no preface. Include concise answer, deep dive, pitfalls, sample Q/A, and concrete code-level examples where relevant.";
        var userPrompt = $"Generate an interview aid markdown doc for topic: {topic}\n\nTranscript context:\n{transcriptContext}\n\nConstraints: Use headings, bullets, and short paragraphs; keep high signal and practical depth.";

        var payload = new
        {
            model = loadout.ModelId,
            stream = true,
            temperature = Math.Min(0.6f, Math.Max(0.15f, loadout.Temperature)),
            max_tokens = Math.Max(700, loadout.MaxTokens * 3),
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        await foreach (var chunk in SendStreamingChatAsync(loadout, payload, cancellationToken))
        {
            yield return chunk;
        }
    }

    private static async IAsyncEnumerable<string> SendStreamingChatAsync(
        ProviderLoadout loadout,
        object payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrWhiteSpace(loadout.Endpoint)
            ? "https://api.openai.com/v1/chat/completions"
            : loadout.Endpoint.Trim();

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(loadout.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loadout.ApiKey.Trim());
        }

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var summarizedBody = SummarizeProviderBody(body);
            throw new HttpRequestException($"Provider error ({(int)response.StatusCode}): {summarizedBody}");
        }

        if (!IsServerSentEvent(response.Content.Headers.ContentType?.MediaType))
        {
            var nonStreamingText = ExtractMessageContent(body);
            if (!string.IsNullOrWhiteSpace(nonStreamingText))
            {
                yield return nonStreamingText;
            }

            yield break;
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payloadLine = line[5..].Trim();
            if (payloadLine == "[DONE]")
            {
                yield break;
            }

            if (string.IsNullOrWhiteSpace(payloadLine))
            {
                continue;
            }

            string? chunkText = null;
            try
            {
                using var json = JsonDocument.Parse(payloadLine);
                if (json.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var first = choices[0];
                    if (first.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var deltaContent) && deltaContent.ValueKind == JsonValueKind.String)
                    {
                        chunkText = deltaContent.GetString();
                    }
                    else if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var messageContent) && messageContent.ValueKind == JsonValueKind.String)
                    {
                        chunkText = messageContent.GetString();
                    }
                }
            }
            catch
            {
                chunkText = null;
            }

            if (!string.IsNullOrEmpty(chunkText))
            {
                yield return chunkText;
            }
        }
    }

    private static bool IsServerSentEvent(string? mediaType)
    {
        return string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static object[] BuildUserContent(string promptText, IReadOnlyList<FrameSnapshot> keyframes)
    {
        var list = new List<object>
        {
            new
            {
                type = "text",
                text = promptText,
            },
        };

        foreach (var frame in keyframes)
        {
            list.Add(new
            {
                type = "image_url",
                image_url = new
                {
                    url = $"data:image/jpeg;base64,{Convert.ToBase64String(frame.JpegBytes)}",
                    detail = "low",
                },
            });
        }

        return list.ToArray();
    }

    private static string BuildRecommendationSystemPrompt()
    {
        return """
You are a real-time interview co-pilot that outputs topic recommendation chips only.

Hard rules:
1) Output must be plain text lines.
2) Each line = exactly one concise chip phrase (3-8 words).
3) No numbering, no markdown headers, no explanations.
4) Avoid duplicates and near-duplicates of provided existing chips.
5) Prefer actionable, interview-ready prompts tied to the immediate context.
6) Prioritize technologies, concepts, architecture, debugging, performance, and follow-up questions.
7) Return at most 10 chips.
8) Keep chips compact and high signal.

Behavior tuning:
- Use transcript semantics first, screenshot clues second.
- If context is ambiguous, propose clarifying chips that help answer strongly.
- Include at least one strategic chip and one technical deep-dive chip.
""";
    }

    private static string BuildRecommendationUserPrompt(
        string transcriptContext,
        IReadOnlyList<FrameSnapshot> keyframes,
        IReadOnlyList<string> existingChips)
    {
        var frameSummary = keyframes.Count == 0
            ? "No keyframes attached"
            : string.Join('\n', keyframes.Select(static frame => frame.ToSummaryLine()));

        var chips = existingChips.Count == 0
            ? "(none)"
            : string.Join(" | ", existingChips);

        return $"""
Create recommendation chips from this live context.

Transcript (last 30s):
{transcriptContext}

Keyframes:
{frameSummary}

Existing chips:
{chips}
""";
    }

    private static string ExtractMessageContent(string responseBody)
    {
        using var json = JsonDocument.Parse(responseBody);
        if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        var first = choices[0];
        if (first.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string SummarizeProviderBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "empty response body";
        }

        string text;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    text = error.GetString() ?? string.Empty;
                }
                else if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                {
                    text = message.GetString() ?? string.Empty;
                }
                else
                {
                    text = error.ToString();
                }
            }
            else
            {
                text = body;
            }
        }
        catch
        {
            text = body;
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length > 220)
        {
            text = text[..220] + "…";
        }

        return text;
    }
}