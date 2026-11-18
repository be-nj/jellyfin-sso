using Jellyfin.Plugin.Sso.Controllers;

namespace Jellyfin.Plugin.Sso.Tests;

/// <summary>
/// What a typed Quick Connect code may look like before it is handed to Jellyfin: the six
/// digits Jellyfin issues, however the person spaced them.
/// </summary>
public sealed class TypedQuickConnectCodeTests
{
    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123456 ", "123456")]
    [InlineData("123 456", "123456")]
    [InlineData("123-456", "123456")]
    public void Six_digits_are_a_code_however_they_are_spaced(string typed, string code)
        => Assert.Equal(code, SsoCallbackController.TypedCode(typed));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("١٢٣٤٥٦")]
    [InlineData("123456; DROP")]
    public void Anything_else_never_reaches_jellyfin(string? typed)
        => Assert.Null(SsoCallbackController.TypedCode(typed));
}
