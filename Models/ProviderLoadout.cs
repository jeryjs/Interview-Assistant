namespace Naveen_Sir.Models;

public sealed class ProviderLoadout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Primary";
    public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "gpt-4o-mini";
    public string ChipModelId { get; set; } = string.Empty;
    public string TopicModelId { get; set; } = string.Empty;
    public float Temperature { get; set; } = 0.2f;
    public int MaxTokens { get; set; } = 500;

    public ProviderLoadout Clone()
    {
        return new ProviderLoadout
        {
            Id = Id,
            Name = Name,
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            ModelId = ModelId,
            ChipModelId = ChipModelId,
            TopicModelId = TopicModelId,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
        };
    }

    public ProviderLoadout Fork()
    {
        return new ProviderLoadout
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"{Name} Copy",
            Endpoint = Endpoint,
            ApiKey = ApiKey,
            ModelId = ModelId,
            ChipModelId = ChipModelId,
            TopicModelId = TopicModelId,
            Temperature = Temperature,
            MaxTokens = MaxTokens,
        };
    }

    public static ProviderLoadout CreateDefault()
    {
        return new ProviderLoadout
        {
            Name = "Default OpenAI-Compatible",
            Endpoint = "https://api.openai.com/v1/chat/completions",
            ApiKey = string.Empty,
            ModelId = "gpt-4o-mini",
            ChipModelId = "gpt-4o-mini",
            TopicModelId = "gpt-4.1",
            Temperature = 0.2f,
            MaxTokens = 500,
        };
    }

    public string ResolveChipModelId()
    {
        return string.IsNullOrWhiteSpace(ChipModelId) ? ModelId : ChipModelId;
    }

    public string ResolveTopicModelId()
    {
        return string.IsNullOrWhiteSpace(TopicModelId) ? ModelId : TopicModelId;
    }
}