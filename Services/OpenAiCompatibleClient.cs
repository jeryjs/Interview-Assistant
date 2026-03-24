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
        string modelId,
        string transcriptContext,
        IReadOnlyList<FrameSnapshot> keyframes,
        IReadOnlyList<string> existingChips,
        bool includeImages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var systemPrompt = BuildRecommendationSystemPrompt();
        var userText = BuildRecommendationUserPrompt(transcriptContext, keyframes, existingChips);
        var userContent = BuildUserContent(userText, keyframes, includeImages);

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(modelId) ? loadout.ModelId : modelId,
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
        string modelId,
        string topic,
        string transcriptContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var systemPrompt = """
You are an elite real-time technical interview strategist. Produce an "ultimate response": concise first, then layered depth, always practical.

Primary objective:
- Help the candidate answer confidently in the current interview moment with high signal, low fluff, and interviewer-aware framing.

Output contract:
1) Return markdown only.
2) Start with a 20-30 second answer block.
3) Then provide deep technical explanation with architecture-level reasoning.
4) Include concrete tradeoffs, failure modes, edge cases, and measurable metrics.
5) Include implementation snippets/pseudocode only when useful.
6) Include interviewer follow-up simulation (likely questions + strong answers).
7) Include "what NOT to say" pitfalls.
8) Include a final rapid-fire memory checklist.

Quality constraints:
- No generic filler.
- Prefer crisp, interview-safe phrasing.
- Explicitly distinguish when advice is situational.
- Give decision frameworks, not just facts.
- Emphasize production realism: latency, reliability, scale, debuggability, testability.

Formatting constraints:
- Use concise headings.
- Use bullets heavily.
- Keep each bullet high information density.
- Do not include preambles like "Sure" or "Here is".
""";

        var userPrompt = $"""
Create an interview response dossier for topic: `{topic}`

Current conversation context:
```context
{transcriptContext}
```

Priorities:
- Maximize practical usefulness for answering right now.
- Cover both short answer and deep explanation.
- Include likely interviewer follow-ups and robust responses.
- Include examples the candidate can adapt instantly.
""";

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(modelId) ? loadout.ModelId : modelId,
            stream = true,
            // We wont be passing these as not all endpoints support them, and the defaults are usually good enough for this use case
            // temperature = Math.Min(0.6f, Math.Max(0.15f, loadout.Temperature)),
            // max_tokens = Math.Max(700, loadout.MaxTokens * 3),
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

    private static object[] BuildUserContent(string promptText, IReadOnlyList<FrameSnapshot> keyframes, bool includeImages)
    {
        var list = new List<object>
        {
            new
            {
                type = "text",
                text = promptText,
            },
        };

        if (!includeImages || keyframes.Count == 0)
        {
            return list.ToArray();
        }

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
You produce recommendation chips for live interviews.

Your default action is restraint: emit nothing unless there is clear, high-value signal.

Hard output format:
1) Plain text lines only.
2) Each line is one chip, 1-4 words.
3) No numbering, no punctuation decorations, no explanations.
4) Max 4 chips total. Min 1. Recommended: 1. If more is strongly warranted then 2 or 3, rarely 4.
5) If no strong recommendation exists, output exactly: NO_CHIPS

Decision policy:
- Generate chips only when they materially improve the next answer.
- Reject vague, repetitive, or low-specificity topics.
- Avoid synonyms of existing chips.
- Prefer focused, answer-ready prompts over broad categories.
- Favor immediate follow-up depth over generic review topics.

Compression policy:
- Make chip titles compact and direct.
- Remove stopwords when possible.
- Avoid long multi-clause phrases.
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

Transcript (recent context window):
{transcriptContext}

Keyframes:
{frameSummary}

Existing chips:
{chips}

Remember: output NO_CHIPS when no strongly useful chip is warranted.
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