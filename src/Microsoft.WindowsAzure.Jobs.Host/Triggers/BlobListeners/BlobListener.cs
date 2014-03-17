﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.WindowsAzure.StorageClient;

namespace Microsoft.WindowsAzure.Jobs
{
    // Hybrid blob listening. 
    // Will do a full scan of all containers on startup 
    // And then listens for logs in steady-state. Note that log listening can have a 10+ minute delay. 
    // Not deterministic when the blobs show up in the logs. 
    internal class BlobListener : IBlobListener
    {
        CloudBlobContainer[] _containers;
        BlobLogListener[] _clientListeners; // for log listening

        // Union of container names across all clients. For fast filtering. 
        // $$$ could optimize this by making it client specific. 
        HashSet<string> _containerNames = new HashSet<string>();

        bool _completedFullScanOnStartup;
        CancellationToken _backgroundCancel; // cancellation token for the full scan running in the background

        public BlobListener(IEnumerable<CloudBlobContainer> containers)
        {
            _containers = containers.ToArray();

            // Get unique set of clients for the containers. 
            var clients = new HashSet<CloudBlobClient>(new CloudBlobClientComparer());
            foreach (var container in containers)
            {
                _containerNames.Add(container.Name);
                clients.Add(container.ServiceClient);
            }


            List<BlobLogListener> x = new List<BlobLogListener>();

            foreach (var client in clients)
            {
                try
                {
                    var ll = new BlobLogListener(client);
                    x.Add(ll);
                }
                catch
                {
                    // Likely that client credentials were not valid. 
                }
            }

            _clientListeners = x.ToArray();
        }

        // Need to do a full scan on startup 
        // This can take a while so kick off on background thread and queues results to main thread?        
        void InitialScan()
        {
            // Include cancellation

            foreach (var container in _containers)
            {
                Thread t = new Thread(_ => FullScanContainer(container));
                t.Start();
            }
        }

        ConcurrentQueue<CloudBlob> _queueExistingBlobs = new ConcurrentQueue<CloudBlob>();

        private void FullScanContainer(CloudBlobContainer container)
        {
            try
            {
                container.CreateIfNotExist();
            }
            catch (StorageException)
            {
                // Can happen if container was deleted (or being deleted). Just ignore and return empty collection.
                // If it's deleted, there's nothing to listen for.           
                return;
            }

            var opt = new BlobRequestOptions();
            opt.UseFlatBlobListing = true;
            foreach (var blobItem in container.ListBlobs(opt))
            {
                if (_backgroundCancel.IsCancellationRequested)
                {
                    return;
                }

                CloudBlob b = container.GetBlobReference(blobItem.Uri.ToString());

                _queueExistingBlobs.Enqueue(b);
            }
        }


        // Blob could have been deleted by the time the callback is invoked. 
        // - race where it was explicitly deleted
        // - if we detected blob via a log, then there's a long window (possibly hours) where it could have easily been deleted. 
        public void Poll(Action<CloudBlob, CancellationToken> callback, CancellationToken cancel)
        {
            if (!_completedFullScanOnStartup)
            {
                _backgroundCancel = cancel;
                _completedFullScanOnStartup = true;
                InitialScan();
            }

            // Drain the background queues. 
            while (true)
            {
                CloudBlob b;
                if (!_queueExistingBlobs.TryDequeue(out b))
                {
                    break;
                }
                callback(b, cancel);
            }

            // Listen on logs for new events. 
            foreach (var client in _clientListeners)
            {
                if (cancel.IsCancellationRequested)
                {
                    return;
                }

                foreach (var path in client.GetRecentBlobWrites())
                {
                    if (cancel.IsCancellationRequested)
                    {
                        return;
                    }

                    // Log listening is Client wide. So filter out things that aren't in the container list. 
                    if (!_containerNames.Contains(path.ContainerName))
                    {
                        continue;
                    }

                    var blob = path.Resolve(client.Client);

                    callback(blob, cancel);
                }
            }
        }
    }
}