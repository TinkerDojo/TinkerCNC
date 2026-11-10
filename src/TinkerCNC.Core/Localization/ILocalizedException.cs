namespace TinkerCNC.Localization;

/// <summary>An error whose message is meant for people; the dashboard shows <see cref="Text"/> in its language.</summary>
public interface ILocalizedException
{
    Text Text { get; }
}

/// <summary>A request that cannot be done now, with the reason for people (its message is the English one).</summary>
public class LocalizedException(Text text) : InvalidOperationException(text.ToString()), ILocalizedException
{
    public Text Text { get; } = text;
}

/// <summary>The machine did not answer or get there in time; the reason for people as in <see cref="LocalizedException"/>.</summary>
public class LocalizedTimeoutException(Text text) : TimeoutException(text.ToString()), ILocalizedException
{
    public Text Text { get; } = text;
}
