namespace BedrockSentry.Core.Structures;

/// <summary>The world's ticking areas as read from the console, and when (<c>D107</c>).</summary>
public sealed record TickingAreaRead(TickingAreaList List, DateTimeOffset ReadUtc);
