﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Threading;
using Microsoft.WindowsAzure.Storage;

namespace Microsoft.Azure.WebJobs.Host.FunctionalTests.TestDoubles
{
    internal sealed class CompletedCancellableAsyncResult : ICancellableAsyncResult, IDisposable
    {
        private readonly object _state;

        private bool _disposed;
        private WaitHandle _waitHandle;

        public CompletedCancellableAsyncResult(object state)
        {
            _state = state;
        }

        public object AsyncState
        {
            get
            {
                ThrowIfDisposed();
                return _state;
            }
        }

        public WaitHandle AsyncWaitHandle
        {
            get
            {
                ThrowIfDisposed();

                // Lazily allocate
                if (_waitHandle == null)
                {
                    _waitHandle = new ManualResetEvent(initialState: true);
                }

                return _waitHandle;
            }
        }

        public bool CompletedSynchronously
        {
            get
            {
                ThrowIfDisposed();
                return true;
            }
        }

        public bool IsCompleted
        {
            get
            {
                ThrowIfDisposed();
                return true;
            }
        }

        public void Cancel()
        {
            ThrowIfDisposed();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_waitHandle != null)
                {
                    _waitHandle.Dispose();
                }

                _disposed = true;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(null);
            }
        }
    }
}