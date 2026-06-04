using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace SkyScope.UI;

internal static class ClipboardHelper
{
    // WPF's Clipboard.SetText blocks the calling thread while it retries an OLE clipboard that is
    // momentarily locked by another process (clipboard managers, antivirus) — up to ~1s internally.
    // Doing that on the UI thread freezes the app, so run it on a short-lived background STA thread
    // and report success back on the UI thread. SetText flushes the data (copy = true), so it
    // persists after the worker thread exits.
    public static void SetTextAsync(string text, Action onSuccess)
    {
        var thread = new Thread(() =>
        {
            var ok = false;
            for (var attempt = 0; attempt < 3 && !ok; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    ok = true;
                }
                catch (COMException)
                {
                    Thread.Sleep(50);
                }
            }

            if (ok)
                Application.Current?.Dispatcher.BeginInvoke(onSuccess);
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
