namespace Naveen_Sir.Models;

public enum TopicStreamChannel
{
    Answer = 0,
    Reasoning = 1,
}

public readonly record struct TopicStreamChunk(TopicStreamChannel Channel, string Text);
