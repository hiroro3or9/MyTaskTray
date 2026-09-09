using System.Runtime.ExceptionServices;
using System.Threading;
using MyTaskTray.Models;
using MyTaskTray.Services;
using MyTaskTray.ViewModels;
using Xunit;

namespace MyTaskTray.Tests;

/// <summary>
/// Swap コピーのうち、自動で確かめられる範囲を固定する。
///
/// <para>
/// 入れ替えそのもの（Ctrl+C / Ctrl+V の送信、貼り付け先の反応、待ち時間）は
/// 実機でしか確かめられない。ここで押さえるのは設定の解釈と往復だけ。
/// 実機で確認する項目は DESIGN_SWAP_COPY.md §9 を参照。
/// </para>
/// </summary>
public sealed class SwapCopySettingsTests
{
    [Fact]
    public void SwapCopyHotKeyIsNormalizedForSaving()
    {
        RunOnStaThread(() =>
        {
            SettingsViewModel viewModel = CreateViewModel();
            viewModel.SwapCopyHotKey = "ctrl+alt+s";

            Assert.True(viewModel.TryGetNormalizedSwapCopyHotKey(
                out string normalized, out string error));
            Assert.Equal("Ctrl+Alt+S", normalized);
            Assert.Empty(error);
        });
    }

    [Fact]
    public void EmptySwapCopyHotKeyMeansDisabled()
    {
        RunOnStaThread(() =>
        {
            SettingsViewModel viewModel = CreateViewModel();
            viewModel.SwapCopyHotKey = "   ";

            Assert.True(viewModel.TryGetNormalizedSwapCopyHotKey(
                out string normalized, out string error));
            Assert.Empty(normalized);
            Assert.Empty(error);
            Assert.Contains("無効", viewModel.SwapCopyHotKeyStatus);
        });
    }

    /// <summary>
    /// 修飾キーのないキーは、そのキーが全アプリで打てなくなるため受け付けない。
    /// メニュー用のホットキーと同じ判定を共用していることの確認でもある。
    /// </summary>
    [Fact]
    public void SwapCopyHotKeyWithoutModifierIsRejected()
    {
        RunOnStaThread(() =>
        {
            SettingsViewModel viewModel = CreateViewModel();
            viewModel.SwapCopyHotKey = "S";

            Assert.False(viewModel.TryGetNormalizedSwapCopyHotKey(
                out string normalized, out string error));
            Assert.Empty(normalized);
            Assert.NotEmpty(error);
        });
    }

    [Fact]
    public void ChangingSwapCopyHotKeyMarksSettingsDirty()
    {
        RunOnStaThread(() =>
        {
            SettingsViewModel viewModel = CreateViewModel();
            viewModel.MarkSaved();

            viewModel.SwapCopyHotKey = "Ctrl+Alt+S";

            Assert.True(viewModel.IsDirty);
        });
    }

    [Fact]
    public void SwapCopyHotKeySurvivesTheSettingsRoundTrip()
    {
        RunOnStaThread(() =>
        {
            SettingsViewModel viewModel = CreateViewModel(swapCopyHotKey: "Ctrl+Alt+S");

            Assert.Equal("Ctrl+Alt+S", viewModel.SwapCopyHotKey);

            AppSettings saved = viewModel.ToSettings(
                normalizedMenuHotKey: "Ctrl+Alt+V",
                normalizedSwapCopyHotKey: "Ctrl+Alt+S",
                validatedSprint: null);

            Assert.Equal("Ctrl+Alt+S", saved.SwapCopyHotKey);
            Assert.Equal("Ctrl+Alt+S", saved.Clone().SwapCopyHotKey);
        });
    }

    /// <summary>設定を持たない古い設定ファイルでは、Swap コピーは無効のままになる。</summary>
    [Fact]
    public void SwapCopyIsDisabledByDefault()
        => Assert.Empty(new AppSettings().SwapCopyHotKey);

    [Fact]
    public void EmptySnapshotHasNothingToRestore()
    {
        Assert.True(ClipboardSnapshot.Empty.IsEmpty);
        Assert.Equal(0, ClipboardSnapshot.Empty.FormatCount);
        Assert.Empty(ClipboardSnapshot.Empty.Text);
    }

    /// <summary>
    /// 控えは形式ごとに持ち、代表の文字列とは別に数える。
    /// 文字列を持たない内容（画像だけなど）でも「空」にはならない。
    /// </summary>
    [Fact]
    public void SnapshotKeepsEveryFormatBesideItsText()
    {
        ClipboardSnapshot snapshot = new(
            [
                new ClipboardSnapshot.Entry("UnicodeText", "あいう"),
                new ClipboardSnapshot.Entry("HTML Format", "<p>あいう</p>"),
            ],
            "あいう");

        Assert.False(snapshot.IsEmpty);
        Assert.Equal(2, snapshot.FormatCount);
        Assert.Equal("あいう", snapshot.Text);
    }

    [Fact]
    public void SnapshotWithoutTextIsStillRestorable()
    {
        ClipboardSnapshot snapshot = new(
            [new ClipboardSnapshot.Entry("Bitmap", new object())],
            string.Empty);

        Assert.False(snapshot.IsEmpty);
        Assert.Empty(snapshot.Text);
    }

    private static SettingsViewModel CreateViewModel(string swapCopyHotKey = "")
        => new(new AppSettings
        {
            SwapCopyHotKey = swapCopyHotKey,
            Items =
            [
                new ClipItem
                {
                    Id = "item-1",
                    Name = "テスト項目",
                    Text = "テスト",
                },
            ],
        });

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA テストが時間内に完了しませんでした。");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
