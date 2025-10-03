// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
//
// Permission is hereby granted, free of charge, to any person obtaining a
// copy of this software and associated documentation files (the "Software"),
// to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense,
// and/or sell copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using CoCoL;
using Duplicati.Library.Main.Volumes;
using Duplicati.Library.Utility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Duplicati.Library.Main.Operation.Restore
{

    /// <summary>
    /// Process that manages the volumes that the `VolumeDownloader` process has downloaded.
    /// It is responsible for fetching the volumes from the backend and caching them.
    /// </summary>
    internal class VolumeManager
    {
        /// <summary>
        /// The log tag for this class.
        /// </summary>
        private static readonly string LOGTAG = Logging.Log.LogTagFromType<VolumeManager>();

        /// <summary>
        /// Helper class to read from either of two channels.
        /// This is a workaround for the fact that CoCoL seems to deadlock on ReadFroAnyAsync
        /// </summary>
        /// <param name="channel1">The first channel to read from.</param>
        /// <param name="channel2">The second channel to read from.</param>
        private sealed class ReadFromEither(IReadChannel<object> channel1, IReadChannel<object> channel2)
        {
            /// <summary>
            /// The first task that is reading from the channels.
            /// </summary>
            private Task<object>? t1;
            /// <summary>
            /// The second task that is reading from the channels.
            /// </summary>
            private Task<object>? t2;

            /// <summary>
            /// Reads from either of the two channels asynchronously.
            /// </summary>
            /// <returns>The object read from the channel.</returns>
            public async Task<object> ReadFromEitherAsync(CancellationToken token)
            {
                // NOTE: This is not a correct external choice,
                // as we have actually consumed from both channels,
                // but we only process one of them
                // This is safe here, because the shutdown only happens on failure termination
                t1 ??= channel1.ReadAsync(token);
                t2 ??= channel2.ReadAsync(token);

                var r = await Task.WhenAny(t1, t2).ConfigureAwait(false);
                if (r == t1)
                {
                    t1 = null;
                    return await r.ConfigureAwait(false);
                }
                else
                {
                    t2 = null;
                    return await r.ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Runs the volume manager process.
        /// </summary>
        /// <param name="channels">The named channels for the restore operation.</param>
        /// <param name="options">The restore options.</param>
        public static Task Run(Channels channels, Options options, RestoreResults results)
        {
            return AutomationExtensions.RunTask(
                new
                {
                    VolumeRequest = channels.VolumeRequest.AsRead(),
                    VolumeResponse = channels.VolumeResponse.AsRead(),
                    DecompressRequest = channels.DecompressionRequest.AsWrite(),
                    DecompressAck = channels.DecompressionAck.AsRead(),
                    DownloadRequest = channels.DownloadRequest.AsWrite(),
                },
                async self =>
                {
                    // The maximum number of volumes to have in cache at once. If this is exceeded, we'll try to evict the least recently used volume that is not actively in use.
                    long cache_max = options.RestoreVolumeCacheHint / options.VolumeSize;
                    // Cache of volume readers.
                    Dictionary<long, VolumeWrapper> cache = [];
                    // List of which volume was accessed last. Used for cache eviction.
                    List<long> cache_last_touched = [];
                    // Dictionary to keep track of active downloads. Used for grouping requests to the same volume.
                    Dictionary<long, List<BlockRequest>> in_flight_downloads = [];

                    Stopwatch? sw_cache_set = options.InternalProfiling ? new() : null;
                    Stopwatch? sw_cache_evict = options.InternalProfiling ? new() : null;
                    Stopwatch? sw_cache_lru = options.InternalProfiling ? new() : null;
                    Stopwatch? sw_query = options.InternalProfiling ? new() : null;
                    Stopwatch? sw_backend = options.InternalProfiling ? new() : null;
                    Stopwatch? sw_request = options.InternalProfiling ? new() : null;
                    Stopwatch? sw_wakeup = options.InternalProfiling ? new() : null;

                    void handle_evict(long volume_id)
                    {
                        Logging.Log.WriteExplicitMessage(LOGTAG, "VolumeRequest", "Evicting volume {0} from cache", volume_id);
                        sw_cache_evict?.Start();
                        cache.Remove(volume_id, out var volume);
                        volume?.Dispose();
                        cache_last_touched.Remove(volume_id);
                        sw_cache_evict?.Stop();
                    }

                    void evict_lru()
                    {
                        sw_cache_lru?.Start();
                        if (cache_last_touched.Count > 0)
                        {
                            // Pop the last element of cache_last_touched
                            var volume_id = cache_last_touched[0];
                            cache_last_touched.RemoveAt(0);
                            handle_evict(volume_id);
                        }
                        sw_cache_lru?.Stop();
                    }

                    await results.TaskControl.ProgressRendevouz().ConfigureAwait(false);

                    var rfa = new ReadFromEither(self.VolumeResponse, self.VolumeRequest);
                    try
                    {
                        while (true)
                        {
                            // TODO: CoCol ReadFromAnyAsync deadlocks, so we use a workaround
                            Logging.Log.WriteExplicitMessage(LOGTAG, "VolumeRequest", "Waiting for volume request or response");
                            var msg = await rfa.ReadFromEitherAsync(results.TaskControl.ProgressToken).ConfigureAwait(false);
                            switch (msg)
                            {
                                case BlockRequest request:
                                    switch (request.RequestType)
                                    {
                                        case BlockRequestType.CacheEvict:
                                            {
                                                Logging.Log.WriteExplicitMessage(LOGTAG, "VolumeRequest", "Evicting volume {0} from cache by request", request.VolumeID);
                                                handle_evict(request.VolumeID);
                                            }
                                            break;
                                        case BlockRequestType.Download:
                                            {
                                                Logging.Log.WriteExplicitMessage(LOGTAG, "VolumeRequest", "Got a request for block {0} from volume {1}", request.BlockID, request.VolumeID);
                                                sw_request?.Start();
                                                if (cache.TryGetValue(request.VolumeID, out var volume))
                                                {
                                                    cache_last_touched.Remove(request.VolumeID);
                                                    cache_last_touched.Add(request.VolumeID);
                                                    Logging.Log.WriteExplicitMessage(LOGTAG, "VolumeRequest", "Block {0} found in cache", request.BlockID);
                                                    volume.Reference();
                                                    await self.DecompressRequest.WriteAsync((request, volume)).ConfigureAwait(false);
                                                    Logging.Log.WriteExplicitMessage(LOGTAG, "VolumeRequest", "Requesting decompression of block {0} from cached volume {1}", request.BlockID, request.VolumeID);
                                                }
                                                else
                                                {
                                                    Logging.Log.WriteExplicitMessage(LOGTAG, "VolumeRequest", "Block {0} not found in cache, requesting volume {1}", request.BlockID, request.VolumeID);
                                                    if (in_flight_downloads.TryGetValue(request.VolumeID, out var waiters))
                                                    {
                                                        waiters.Add(request);
                                                    }
                                                    else
                                                    {
                                                        await self.DownloadRequest.WriteAsync(request.VolumeID).ConfigureAwait(false);
                                                        in_flight_downloads[request.VolumeID] = [request];
                                                    }
                                                }
                                                sw_request?.Stop();
                                            }
                                            break;
                                        default:
                                            throw new InvalidOperationException($"Unexpected request type: {request.RequestType}");
                                    }
                                    break;
                                case (long volume_id, TempFile tmpfile, BlockVolumeReader reader):
                                    {
                                        sw_cache_set?.Start();
                                        var volume = new VolumeWrapper(tmpfile, reader);
                                        volume.Reference(in_flight_downloads[volume_id].Count);
                                        if (cache_max > 0)
                                        {
                                            // Check if adding another volume would exceed cache limits.
                                            if ((cache.Count + 1) > cache_max)
                                            {
                                                // TODO switch based of the eviction strategy.
                                                // fifo / lifo based on both when they were downloaded and when they were used
                                                // random
                                                // Heuristic based of accesses and recency
                                                // Cache would overflow if we request another; we have to evict something, or store the request for later.
                                                evict_lru();
                                            }
                                            Logging.Log.WriteExplicitMessage(LOGTAG, "VolumeRequest", "Caching volume {0} ({1} + 1 <= {2})", volume_id, cache.Count, cache_max);
                                            cache[volume_id] = volume;
                                        }
                                        else
                                        {
                                            Logging.Log.WriteExplicitMessage(LOGTAG, "VolumeRequest", "Not caching volume {0} ({1} + 1 > {2})", volume_id, cache.Count, cache_max);
                                            volume.Dispose();
                                        }
                                        cache_last_touched.Add(volume_id);
                                        sw_cache_set?.Stop();
                                        sw_wakeup?.Start();
                                        foreach (var request in in_flight_downloads[volume_id])
                                        {
                                            // Request the decompressions
                                            Logging.Log.WriteExplicitMessage(LOGTAG, "VolumeRequest", "Requesting block {0} from newly cached volume {1}", request.BlockID, volume_id);
                                            await self.DecompressRequest.WriteAsync((request, volume)).ConfigureAwait(false);
                                        }
                                        in_flight_downloads.Remove(volume_id);
                                        sw_wakeup?.Stop();
                                        break;
                                    }
                                default:
                                    throw new InvalidOperationException("Unexpected message type");
                            }
                        }
                    }
                    catch (RetiredException)
                    {
                        Logging.Log.WriteVerboseMessage(LOGTAG, "RetiredProcess", null, "Volume manager retired");

                        if (options.InternalProfiling)
                        {
                            Logging.Log.WriteProfilingMessage(LOGTAG, "InternalTimings", $"CacheSet: {sw_cache_set?.ElapsedMilliseconds}ms, CacheEvict: {sw_cache_evict?.ElapsedMilliseconds}ms, Query: {sw_query?.ElapsedMilliseconds}ms, Backend: {sw_backend?.ElapsedMilliseconds}ms, Request: {sw_request?.ElapsedMilliseconds}ms, Wakeup: {sw_wakeup?.ElapsedMilliseconds}ms");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logging.Log.WriteErrorMessage(LOGTAG, "VolumeManagerError", ex, "Error during volume manager");
                        throw;
                    }
                }
            );
        }
    }

}