using System;
using System.Threading;
using System.Threading.Tasks;

namespace WinIsland.App.Core
{
    public class HoverIntentController : IDisposable
    {
        private CancellationTokenSource? _hoverCts;
        private CancellationTokenSource? _collapseCts;
        private readonly object _lock = new object();

        public int HoverDelayMs { get; set; } = 250;
        public int CollapseDelayMs { get; set; } = 300;
        public bool RequireAltKey { get; set; } = false;

        public bool IsExpanded { get; private set; }
        public bool IsPendingExpand { get; private set; }

        public event Action? ExpandRequested;
        public event Action? CollapseRequested;
        public event Action? FileDropExpandRequested;

        public async Task TriggerMouseEnterAsync(bool isAltDown = false)
        {
            // If modifier key is required and Alt is not held, do nothing
            if (RequireAltKey && !isAltDown)
            {
                return;
            }

            lock (_lock)
            {
                _collapseCts?.Cancel();
                _collapseCts?.Dispose();
                _collapseCts = null;

                if (IsExpanded) return;

                _hoverCts?.Cancel();
                _hoverCts?.Dispose();
                _hoverCts = new CancellationTokenSource();
                IsPendingExpand = true;
            }

            var token = _hoverCts.Token;

            try
            {
                await Task.Delay(HoverDelayMs, token);

                lock (_lock)
                {
                    if (token.IsCancellationRequested) return;
                    IsPendingExpand = false;
                    IsExpanded = true;
                }

                ExpandRequested?.Invoke();
            }
            catch (TaskCanceledException)
            {
                lock (_lock)
                {
                    IsPendingExpand = false;
                }
            }
        }

        public void TriggerMouseLeave()
        {
            lock (_lock)
            {
                if (IsPendingExpand)
                {
                    _hoverCts?.Cancel();
                    _hoverCts?.Dispose();
                    _hoverCts = null;
                    IsPendingExpand = false;
                }

                if (!IsExpanded) return;

                _collapseCts?.Cancel();
                _collapseCts?.Dispose();
                _collapseCts = new CancellationTokenSource();
            }

            var token = _collapseCts.Token;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(CollapseDelayMs, token);

                    lock (_lock)
                    {
                        if (token.IsCancellationRequested) return;
                        IsExpanded = false;
                    }

                    CollapseRequested?.Invoke();
                }
                catch (TaskCanceledException)
                {
                    // Collapse cancelled (e.g. mouse re-entered)
                }
            });
        }

        public void TriggerDragEnter()
        {
            lock (_lock)
            {
                _hoverCts?.Cancel();
                _hoverCts?.Dispose();
                _hoverCts = null;
                IsPendingExpand = false;

                _collapseCts?.Cancel();
                _collapseCts?.Dispose();
                _collapseCts = null;

                IsExpanded = true;
            }

            // Zero-delay immediate expand for file drag
            FileDropExpandRequested?.Invoke();
            ExpandRequested?.Invoke();
        }

        public void TriggerDragLeave()
        {
            TriggerMouseLeave();
        }

        public void CollapseImmediately()
        {
            lock (_lock)
            {
                _hoverCts?.Cancel();
                _collapseCts?.Cancel();
                IsPendingExpand = false;
                IsExpanded = false;
            }
            CollapseRequested?.Invoke();
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _hoverCts?.Cancel();
                _hoverCts?.Dispose();
                _hoverCts = null;

                _collapseCts?.Cancel();
                _collapseCts?.Dispose();
                _collapseCts = null;
            }
        }
    }
}
