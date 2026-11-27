using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Moq;
using Windows.Graphics;
using WinOptimizer.Core.Features.Common.Interfaces;
using WinOptimizer.Core.Features.Common.Models;
using WinOptimizer.UI.Features.Common.Dialogs;
using WinOptimizer.UI.Features.Common.Helpers;

namespace WinOptimizer.PopupTests;

public sealed partial class PreviewApplication
{
    /// <summary>Checks the native terminal dialog with short output, live overflow and window resizing.</summary>
    private async Task CheckTaskOutputAsync(Grid shell)
    {
        var root = shell.XamlRoot;
        var window = _window!.AppWindow;
        var originalSize = window.Size;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var localization = new Mock<ILocalizationService>();
        localization.Setup(service => service.GetString("Button_Close")).Returns("Close");
        localization.Setup(service => service.GetString("Button_CopyToClipboard")).Returns("Copy to Clipboard");
        var tasks = new Mock<ITaskProgressService>();
        tasks.SetupGet(service => service.IsTaskRunning).Returns(true);
        var builder = new TaskOutputDialogBuilder(localization.Object, tasks.Object);
        var dialog = builder.Build("Terminal Output", ["Reading devices…", "Checking sources…", "Verifying packages…"]);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        dialog.Opened += (_, _) => opened.TrySetResult();
        var showing = PopupHandler.RunModalAsync(() => PopupHandler.ShowDialogAsync(dialog, root,
            () => builder.StartLiveUpdates(dispatcher), builder.StopLiveUpdates));
        try
        {
            await opened.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var frame = VisualTreeHelper.GetOpenPopupsForXamlRoot(root).SelectMany(popup => Tree<Border>(popup.Child))
                .Single(border => border.Name == "BackgroundElement");
            var terminal = (ScrollViewer)((Border)dialog.Content).Child;
            frame.UpdateLayout();
            var shortHeight = frame.ActualHeight;
            Require(frame.ActualWidth is > 0 and <= 761 && shortHeight is > 0 and < 300 && terminal.ScrollableHeight < 1,
                $"Short terminal output did not fit its content: {frame.ActualWidth} x {shortHeight}.");

            var longOutput = string.Join("\n", Enumerable.Range(1, 50).Select(index => $"Log entry {index}")
                .Append(new string('x', 220)));
            tasks.Raise(service => service.ProgressUpdated += null, this, new TaskProgressDetail { TerminalOutput = longOutput });
            var appended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dispatcher.TryEnqueue(() => appended.TrySetResult());
            await appended.Task.WaitAsync(TimeSpan.FromSeconds(3));
            frame.UpdateLayout();
            Require(frame.ActualWidth <= 761 && frame.ActualHeight > shortHeight && frame.ActualHeight <= 481 &&
                terminal.ScrollableHeight > 0 && terminal.VerticalOffset >= terminal.ScrollableHeight - 1,
                $"Live terminal output did not scroll within the size limit: {frame.ActualWidth} x {frame.ActualHeight}, offset {terminal.VerticalOffset} of {terminal.ScrollableHeight}.");

            var resized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnResized(object sender, SizeChangedEventArgs args) => resized.TrySetResult();
            shell.SizeChanged += OnResized;
            try
            {
                window.ResizeClient(new SizeInt32(580, 400));
                await resized.Task.WaitAsync(TimeSpan.FromSeconds(3));
                frame.UpdateLayout();
                Require(frame.ActualWidth <= root.Size.Width + 1 && frame.ActualHeight <= root.Size.Height + 1 &&
                    terminal.ViewportHeight > 0 && terminal.ScrollableHeight > 0,
                    "The terminal output and its scroll viewport must fit the resized window.");
            }
            finally { shell.SizeChanged -= OnResized; }
        }
        finally
        {
            dialog.Hide();
            await showing.WaitAsync(TimeSpan.FromSeconds(3));
            window.Resize(originalSize);
        }
        Console.WriteLine("PASS: compact terminal output, scrollable live logs and responsive dialog bounds.");
    }
}
