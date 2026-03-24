namespace Naveen_Sir.Models;

public sealed class ProviderLoadout
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Primary";
    public string Endpoint { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string ApiKey { get; set; } = string.Empty;
    public string ModelId { get; set; } = "gpt-4o-mini";
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
            Temperature = 0.2f,
            MaxTokens = 500,
        };
    }
}