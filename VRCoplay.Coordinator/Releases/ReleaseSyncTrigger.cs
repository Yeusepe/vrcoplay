// Copyright (c) 2026 YUCP Studio. VRCoplay contributors retain their copyrights.
// SPDX-License-Identifier: GPL-3.0-or-later
public sealed class ReleaseSyncTrigger
{
    private readonly SemaphoreSlim signal = new(0, 1);
    public void Trigger()
    {
        try { signal.Release(); }
        catch (SemaphoreFullException) { }
    }
    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) =>
        signal.WaitAsync(timeout, cancellationToken);
}
