namespace Luma.Host.Services;

internal static class WindowActivation
{
    internal static bool RestoreAndActivate(Func<bool> minimized, Action restore, Func<bool> activate,
        Func<bool> foreground, Action<int>? delay = null, Func<bool>? stillAuthorized = null)
    {
        delay ??= Thread.Sleep;
        if (stillAuthorized?.Invoke() == false) return false;
        if (minimized())
        {
            restore();
            // ShowWindowAsync posts to the target queue. The target may not have
            // processed SW_RESTORE when it returns; do not race that posted work.
            for (var attempt = 0; attempt < 10 && minimized(); attempt++)
            {
                if (stillAuthorized?.Invoke() == false) return false;
                delay(20);
            }
        }
        if (stillAuthorized?.Invoke() == false) return false;
        activate();
        if (!minimized() && foreground()) return true;
        // Foreground switching can also complete asynchronously across input queues.
        // This is a single user-triggered operation, never an idle/background poll.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            delay(20);
            if (!minimized() && foreground()) return true;
        }
        return false;
    }
}
