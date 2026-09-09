using MyTaskTray.Services;
using Xunit;

namespace MyTaskTray.Tests;

/// <summary>
/// ホットキーの解釈を固定する。
/// 線引きの理由は DESIGN_HOTKEY.md §4 / §4-1 を参照。
/// </summary>
public sealed class HotKeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+V", "Ctrl+Alt+V")]
    [InlineData("alt+ctrl+s", "Ctrl+Alt+S")]
    [InlineData("ctrl+shift+f12", "Ctrl+Shift+F12")]
    [InlineData("win+1", "Win+1")]
    public void ModifiedKeysAreNormalizedToOneSpelling(string input, string expected)
    {
        Assert.True(HotKeyGesture.TryParse(input, out HotKeyGesture gesture, out string error));
        Assert.Equal(expected, gesture.DisplayName);
        Assert.Empty(error);
    }

    /// <summary>
    /// 全角のまま打たれても解釈する。日本語入力を切り忘れると見た目がほぼ同じで、
    /// 解釈だけ失敗するため利用者には理由が分からない。
    /// </summary>
    [Fact]
    public void FullWidthInputIsAccepted()
    {
        Assert.True(HotKeyGesture.TryParse("Ｃｔｒｌ＋Ａｌｔ＋Ｖ", out HotKeyGesture gesture, out _));
        Assert.Equal("Ctrl+Alt+V", gesture.DisplayName);
    }

    /// <summary>文字を入力しないキーは、修飾キーなしでも指定できる。</summary>
    [Theory]
    [InlineData("無変換", "無変換")]
    [InlineData("muhenkan", "無変換")]
    [InlineData("nonconvert", "無変換")]
    [InlineData("変換", "変換")]
    [InlineData("henkan", "変換")]
    [InlineData("アプリケーション", "アプリケーション")]
    [InlineData("apps", "アプリケーション")]
    [InlineData("pause", "Pause")]
    [InlineData("break", "Pause")]
    [InlineData("F13", "F13")]
    [InlineData("f24", "F24")]
    public void KeysThatTypeNothingCanStandAlone(string input, string expected)
    {
        Assert.True(HotKeyGesture.TryParse(input, out HotKeyGesture gesture, out string error));
        Assert.Equal(expected, gesture.DisplayName);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("無変換", 0x1Du)]
    [InlineData("変換", 0x1Cu)]
    [InlineData("apps", 0x5Du)]
    [InlineData("pause", 0x13u)]
    [InlineData("F13", 0x7Cu)]
    [InlineData("F24", 0x87u)]
    public void StandaloneKeysMapToTheirVirtualKey(string input, uint expected)
    {
        Assert.True(HotKeyGesture.TryParse(input, out HotKeyGesture gesture, out _));
        Assert.Equal(expected, gesture.VirtualKey);
        Assert.Equal(0u, gesture.Modifiers);
    }

    /// <summary>
    /// 文字を打つキーの単体指定は断る。登録すると全アプリでそのキーが打てなくなり、
    /// 設定画面でも打てないため元に戻せない。
    /// </summary>
    [Theory]
    [InlineData("V")]
    [InlineData("1")]
    [InlineData("F12")]
    [InlineData("Shift+V")]
    public void KeysThatTypeSomethingNeedAModifier(string input)
    {
        Assert.False(HotKeyGesture.TryParse(input, out _, out string error));
        Assert.NotEmpty(error);
    }

    /// <summary>
    /// 状態を持つキーは解釈しない。反転はホットキー処理より下で起きるため、
    /// 登録できても押すたびに状態がずれる（DESIGN_HOTKEY.md §4-1）。
    /// </summary>
    [Theory]
    [InlineData("CapsLock")]
    [InlineData("NumLock")]
    [InlineData("ScrollLock")]
    [InlineData("F25")]
    [InlineData("半角/全角")]
    public void ToggleKeysAndUnknownNamesAreRejected(string input)
    {
        Assert.False(HotKeyGesture.TryParse(input, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl+Ctrl+V")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Alt")]
    public void MalformedInputIsRejectedWithAReason(string input)
    {
        Assert.False(HotKeyGesture.TryParse(input, out _, out string error));
        Assert.NotEmpty(error);
    }
}
