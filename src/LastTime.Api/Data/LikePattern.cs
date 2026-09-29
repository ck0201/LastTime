namespace LastTime.Api.Data;

public static class LikePattern
{
    public const string EscapeChar = "\\";

    /// <summary>Builds a "contains" pattern where user-typed % and _ are matched literally.</summary>
    public static string Contains(string text) =>
        "%" + text.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
