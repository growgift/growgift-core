namespace GrowGift.SharedKernel.Guards;

public static class Guard
{
    public static string AgainstNullOrWhiteSpace(string? value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException($"{name} must not be empty.", name)
            : value.Trim();

    public static T AgainstNull<T>(T? value, string name) where T : class =>
        value ?? throw new ArgumentNullException(name);

    public static decimal AgainstNegative(decimal value, string name) =>
        value < 0 ? throw new ArgumentOutOfRangeException(name, $"{name} must not be negative.") : value;

    public static int AgainstOutOfRange(int value, int min, int max, string name) =>
        value < min || value > max
            ? throw new ArgumentOutOfRangeException(name, $"{name} must be between {min} and {max}.")
            : value;
}
