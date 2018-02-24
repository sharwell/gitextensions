using System;
using System.Threading;
using System.Threading.Tasks;
using GitUI;
using Microsoft.VisualStudio.Threading;

namespace GitCommands
{
    public class AsyncLoader : IDisposable
    {
        private CancellationTokenSource _cancelledTokenSource;
        public TimeSpan Delay { get; set; }

        public AsyncLoader()
        {
            Delay = TimeSpan.Zero;
        }

        public event EventHandler<AsyncErrorEventArgs> LoadingError = delegate { };

        /// <summary>
        /// Does something on threadpool, executes continuation on current sync context thread, executes onError if the async request fails.
        /// There does probably exist something like this in the .NET library, but I could not find it. //cocytus
        /// </summary>
        /// <typeparam name="T">Result to be passed from doMe to continueWith</typeparam>
        /// <param name="doMe">The stuff we want to do. Should return whatever continueWith expects.</param>
        /// <param name="continueWith">Do this on original sync context.</param>
        /// <param name="onError">Do this on original sync context if doMe barfs.</param>
        public static Task<T> DoAsync<T>(Func<Task<T>> doMeAsync, Func<T, Task> continueWithAsync, Action<AsyncErrorEventArgs> onError)
        {
            AsyncLoader loader = new AsyncLoader();
            loader.LoadingError += (sender, e) => onError(e);
            return loader.LoadAsync(doMeAsync, continueWithAsync);
        }

        public static Task<T> DoAsync<T>(Func<Task<T>> doMeAsync, Func<T, Task> continueWithAsync)
        {
            AsyncLoader loader = new AsyncLoader();
            return loader.LoadAsync(doMeAsync, continueWithAsync);
        }

        public static Task DoAsync(Func<Task> doMeAsync, Func<Task> continueWithAsync)
        {
            AsyncLoader loader = new AsyncLoader();
            return loader.LoadAsync(doMeAsync, continueWithAsync);
        }

        public Task LoadAsync(Func<Task> loadContentAsync, Func<Task> onLoadedAsync)
        { 
            return LoadAsync(token => loadContentAsync(), onLoadedAsync);
        }

        public async Task LoadAsync(Func<CancellationToken, Task> loadContentAsync, Func<Task> onLoadedAsync)
        {
            Cancel();
            if (_cancelledTokenSource != null)
                _cancelledTokenSource.Dispose();
            _cancelledTokenSource = new CancellationTokenSource();
            var token = _cancelledTokenSource.Token;

            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, token).ConfigureAwait(false);
                }
                else
                {
                    await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                }

                if (!token.IsCancellationRequested)
                {
                    await loadContentAsync(token).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException && token.IsCancellationRequested)
                {
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!OnLoadingError(e))
                {
                    throw;
                }

                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!token.IsCancellationRequested)
            {
                try
                {
                    await onLoadedAsync().ConfigureAwait(true);
                }
                catch (Exception e)
                {
                    if (!OnLoadingError(e))
                        throw;
                }
            }
        }

        public Task<T> LoadAsync<T>(Func<Task<T>> loadContentAsync, Func<T, Task> onLoadedAsync)
        {
            return LoadAsync(token => loadContentAsync(), onLoadedAsync);
        }

        public async Task<T> LoadAsync<T>(Func<CancellationToken, Task<T>> loadContentAsync, Func<T, Task> onLoadedAsync)
        {
            Cancel();
            if (_cancelledTokenSource != null)
                _cancelledTokenSource.Dispose();
            _cancelledTokenSource = new CancellationTokenSource();
            var token = _cancelledTokenSource.Token;

            T result;

            try
            {
                if (Delay > TimeSpan.Zero)
                {
                    await Task.Delay(Delay, token).ConfigureAwait(false);
                }
                else
                {
                    await TaskScheduler.Default.SwitchTo(alwaysYield: true);
                }

                if (token.IsCancellationRequested)
                {
                    result = default(T);
                }
                else
                {
                    result = await loadContentAsync(token).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                if (e is OperationCanceledException && token.IsCancellationRequested)
                {
                    return default(T);
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (!OnLoadingError(e))
                {
                    throw;
                }

                return default(T);
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!token.IsCancellationRequested)
            {
                try
                {
                    await onLoadedAsync(result).ConfigureAwait(true);
                }
                catch (Exception e)
                {
                    if (!OnLoadingError(e))
                        throw;

                    return default(T);
                }
            }

            return result;
        }

        public void Cancel()
        {
            if (_cancelledTokenSource != null)
                _cancelledTokenSource.Cancel();
        }

        private bool OnLoadingError(Exception exception)
        {
            var args = new AsyncErrorEventArgs(exception);
            LoadingError(this, args);
            return args.Handled;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
                if (_cancelledTokenSource != null)
                    _cancelledTokenSource.Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    public class AsyncErrorEventArgs : EventArgs
    {
        public AsyncErrorEventArgs(Exception exception)
        {
            Exception = exception;
            Handled = true;
        }

        public Exception Exception { get; private set; }

        public bool Handled { get; set; }
    }
}
