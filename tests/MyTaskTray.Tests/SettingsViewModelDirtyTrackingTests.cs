using System.Runtime.ExceptionServices;
using System.Threading;
using MyTaskTray.Models;
using MyTaskTray.ViewModels;
using Xunit;

namespace MyTaskTray.Tests;

public sealed class SettingsViewModelDirtyTrackingTests
{
    [Fact]
    public void ChangingFormatMarksSettingsDirty()
    {
        RunOnStaThread(() =>
        {
            SettingsViewModel viewModel = CreateViewModel();
            viewModel.MarkSaved();

            viewModel.SelectedItem!.Format = ClipFormat.Markdown;

            Assert.True(viewModel.IsDirty);
        });
    }

    [Fact]
    public void ChangingApplyToEachLineMarksSettingsDirty()
    {
        RunOnStaThread(() =>
        {
            SettingsViewModel viewModel = CreateViewModel(ClipboardMatchKind.HasText);
            viewModel.MarkSaved();

            viewModel.SelectedItem!.ApplyToEachLine = true;

            Assert.True(viewModel.IsDirty);
        });
    }

    [Fact]
    public void AdoptingExternalSequenceValueDoesNotMarkSettingsDirty()
    {
        RunOnStaThread(() =>
        {
            SettingsViewModel viewModel = CreateViewModel();
            viewModel.MarkSaved();

            viewModel.AdoptSequenceValue("item-1", 2);

            Assert.False(viewModel.IsDirty);
            Assert.Equal(2, viewModel.SelectedItem!.SequenceValue);
        });
    }

    private static SettingsViewModel CreateViewModel(
        ClipboardMatchKind clipboardCondition = ClipboardMatchKind.Always)
        => new(new AppSettings
        {
            Items =
            [
                new ClipItem
                {
                    Id = "item-1",
                    Name = "テスト項目",
                    Text = "テスト",
                    ClipboardCondition = clipboardCondition,
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
