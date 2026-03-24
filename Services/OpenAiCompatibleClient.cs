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
        string latestFocus,
        IReadOnlyList<FrameSnapshot> keyframes,
        IReadOnlyList<string> existingChips,
        bool includeImages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(modelId) ? loadout.ModelId : modelId,
            ["stream"] = true,
            // ["temperature"] = loadout.Temperature,
            // ["max_tokens"] = Math.Max(200, loadout.MaxTokens),
            ["messages"] = new object[]
            {
                new
                {
                    role = "system",
                    content = BuildRecommendationSystemPrompt(),
                },
                new
                {
                    role = "user",
                    content = BuildUserContent(
                        BuildRecommendationUserPrompt(transcriptContext, latestFocus, keyframes, existingChips),
                        keyframes,
                        includeImages),
                },
            },
        };

        await foreach (var delta in SendStreamingChatDeltasAsync(loadout, payload, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(delta.AnswerText))
            {
                yield return delta.AnswerText;
            }
        }
    }

    public async IAsyncEnumerable<TopicStreamChunk> StreamTopicStructuredAsync(
        ProviderLoadout loadout,
        string modelId,
        string topic,
        string transcriptContext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(modelId) ? loadout.ModelId : modelId,
            ["stream"] = true, // Required for incremental SSE token rendering.
            ["messages"] = new object[]
            {
                new
                {
                    role = "system",
                    content = BuildTopicSystemPrompt(),
                },
                new
                {
                    role = "user",
                    content = BuildTopicUserPrompt(topic, transcriptContext),
                },
            },
        };

        if (IsNvidiaEndpoint(loadout.Endpoint))
        {
            payload["reasoning_effort"] = "high";
        }

        await foreach (var delta in SendStreamingChatDeltasAsync(loadout, payload, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(delta.ReasoningText))
            {
                yield return new TopicStreamChunk(TopicStreamChannel.Reasoning, delta.ReasoningText);
            }

            if (!string.IsNullOrWhiteSpace(delta.AnswerText))
            {
                yield return new TopicStreamChunk(TopicStreamChannel.Answer, delta.AnswerText);
            }
        }
    }

    private static async IAsyncEnumerable<StreamDelta> SendStreamingChatDeltasAsync(
        ProviderLoadout loadout,
        Dictionary<string, object?> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var endpoint = string.IsNullOrWhiteSpace(loadout.Endpoint)
            ? "https://api.openai.com/v1/chat/completions"
            : loadout.Endpoint.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(loadout.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", loadout.ApiKey.Trim());
        }

        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Provider error ({(int)response.StatusCode}): {SummarizeProviderBody(errorBody)}");
        }

        if (!IsServerSentEvent(response.Content.Headers.ContentType?.MediaType))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var message = ExtractMessageContent(body);
            if (!string.IsNullOrWhiteSpace(message))
            {
                yield return new StreamDelta(message, string.Empty);
            }

            yield break;
        }

        await foreach (var payloadLine in ReadSsePayloadLinesAsync(response, cancellationToken))
        {
            if (!TryExtractDelta(payloadLine, out var delta))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(delta.AnswerText) && string.IsNullOrWhiteSpace(delta.ReasoningText))
            {
                continue;
            }

            yield return delta;
        }
    }

    private static async IAsyncEnumerable<string> ReadSsePayloadLinesAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var eventBuffer = new StringBuilder();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                if (eventBuffer.Length == 0)
                {
                    continue;
                }

                var payloadLine = eventBuffer.ToString().Trim();
                eventBuffer.Clear();

                if (payloadLine == "[DONE]")
                {
                    yield break;
                }

                if (!string.IsNullOrWhiteSpace(payloadLine))
                {
                    yield return payloadLine;
                }

                continue;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            eventBuffer.AppendLine(line[5..].TrimStart());
        }

        if (eventBuffer.Length > 0)
        {
            var finalPayload = eventBuffer.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(finalPayload) && !string.Equals(finalPayload, "[DONE]", StringComparison.Ordinal))
            {
                yield return finalPayload;
            }
        }
    }

    private static bool TryExtractDelta(string payloadLine, out StreamDelta delta)
    {
        delta = default;

        try
        {
            using var json = JsonDocument.Parse(payloadLine);
            if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return false;
            }

            var firstChoice = choices[0];
            var answerText = string.Empty;
            var reasoningText = string.Empty;

            if (firstChoice.TryGetProperty("delta", out var deltaElement))
            {
                answerText = ExtractNamedText(deltaElement, "content");
                reasoningText =
                    ExtractNamedText(deltaElement, "reasoning_content") +
                    ExtractNamedText(deltaElement, "reasoning") +
                    ExtractNamedText(deltaElement, "thinking");
            }

            if (string.IsNullOrWhiteSpace(answerText)
                && string.IsNullOrWhiteSpace(reasoningText)
                && firstChoice.TryGetProperty("message", out var messageElement))
            {
                answerText = ExtractNamedText(messageElement, "content");
                reasoningText =
                    ExtractNamedText(messageElement, "reasoning_content") +
                    ExtractNamedText(messageElement, "reasoning") +
                    ExtractNamedText(messageElement, "thinking");
            }

            delta = new StreamDelta(answerText, reasoningText);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractNamedText(JsonElement container, string propertyName)
    {
        return container.TryGetProperty(propertyName, out var value)
            ? ExtractText(value)
            : string.Empty;
    }

    private static string ExtractText(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Concat(value.EnumerateArray().Select(ExtractText)),
            JsonValueKind.Object when value.TryGetProperty("text", out var text) => ExtractText(text),
            JsonValueKind.Object when value.TryGetProperty("content", out var content) => ExtractText(content),
            JsonValueKind.Object when value.TryGetProperty("value", out var nestedValue) => ExtractText(nestedValue),
            _ => string.Empty,
        };
    }

    private static bool IsServerSentEvent(string? mediaType)
    {
        return string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNvidiaEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return false;
        }

        return endpoint.Contains("integrate.api.nvidia.com", StringComparison.OrdinalIgnoreCase)
            || endpoint.Contains("nvidia.com", StringComparison.OrdinalIgnoreCase);
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

    private static string BuildTopicSystemPrompt()
    {
        return """
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
""";
    }

    private static string BuildTopicUserPrompt(string topic, string transcriptContext)
    {
        return $"""
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
    }

    private static string BuildRecommendationSystemPrompt()
    {
        return """
You produce recommendation chips for live interviews.

Your default action is restraint: emit nothing unless there is clear, high-value signal.

Hard output format:
1) Plain text only.
2) Return chips in ONE line using exact delimiter: |||
3) Format example: chip one ||| chip two ||| chip three
4) Each chip is 1-4 words.
5) No numbering, no bullets, no markdown, no explanations.
6) Max 4 chips total.
7) If no strong recommendation exists, output exactly: NO_CHIPS

Decision policy:
- MAXIMIZE relevance to the latest utterance first; older context is secondary.
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
        string latestFocus,
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

    Latest utterance focus (highest priority):
    {latestFocus}

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
        if (first.TryGetProperty("message", out var message))
        {
            return ExtractNamedText(message, "content");
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

    private readonly record struct StreamDelta(string AnswerText, string ReasoningText);
}
