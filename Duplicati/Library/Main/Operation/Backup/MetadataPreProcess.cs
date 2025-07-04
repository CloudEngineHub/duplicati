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

using System;
using CoCoL;
using System.Threading.Tasks;
using System.IO;
using System.Collections.Generic;
using Duplicati.Library.Interface;
using Duplicati.Library.Main.Operation.Common;
using System.Threading;

namespace Duplicati.Library.Main.Operation.Backup
{
    /// <summary>
    /// This class processes paths for metadata and emits the metadata blocks for storage.
    /// Folders and symlinks in the database, and paths are forwarded to be scanned for changes
    /// </summary>
    internal static class MetadataPreProcess
    {
        private static readonly string FILELOGTAG = Logging.Log.LogTagFromType(typeof(MetadataPreProcess)) + ".FileEntry";

        public class FileEntry
        {
            // From input
            public ISourceProviderEntry Entry;

            // Split
            public long PathPrefixID;
            public string Filename;

            // From database
            public long OldId;
            public DateTime OldModified;
            public long LastFileSize;
            public string OldMetaHash;
            public long OldMetaSize;

            // From filedata
            public DateTime LastWrite;
            public FileAttributes Attributes;

            // After processing metadata
            public IMetahash MetaHashAndSize;
            public bool MetadataChanged;
            public bool TimestampChanged;
        }

        public static Task Run(Channels channels, Options options, BackupDatabase database, long lastfilesetid, ITaskReader taskreader)
        {
            return AutomationExtensions.RunTask(new
            {
                Input = channels.SourcePaths.AsRead(),
                StreamBlockChannel = channels.StreamBlock.AsWrite(),
                Output = channels.ProcessedFiles.AsWrite(),
            },

            async self =>
            {
                var emptymetadata = Utility.WrapMetadata(new Dictionary<string, string>(), options);
                var prevprefix = new KeyValuePair<string, long>(null, -1);

                var CHECKFILETIMEONLY = options.CheckFiletimeOnly;
                var DISABLEFILETIMECHECK = options.DisableFiletimeCheck;

                while (true)
                {
                    var entry = await self.Input.ReadAsync();

                    // We ignore the stop signal, but not the pause and terminate
                    await taskreader.ProgressRendevouz().ConfigureAwait(false);

                    var lastwrite = new DateTime(0, DateTimeKind.Utc);
                    var attributes = entry.IsFolder
                        ? FileAttributes.Directory
                        : FileAttributes.Normal;

                    try
                    {
                        lastwrite = entry.LastModificationUtc;
                    }
                    catch (Exception ex)
                    {
                        LogExceptionHelper.LogCommonWarning(ex, FILELOGTAG, "TimestampReadFailed", entry.Path, "Failed to read timestamp on \"{0}\"");
                    }

                    try
                    {
                        attributes = entry.Attributes;
                    }
                    catch (Exception ex)
                    {
                        LogExceptionHelper.LogCommonWarning(ex, FILELOGTAG, "AttributeReadFailed", entry.Path, "Failed to read attributes on \"{0}\"");
                    }

                    // If we only have metadata, stop here
                    if (await ProcessMetadata(entry, attributes, lastwrite, options, emptymetadata, database, self.StreamBlockChannel, taskreader.ProgressToken).ConfigureAwait(false))
                    {
                        try
                        {
                            var split = Database.LocalDatabase.SplitIntoPrefixAndName(entry.Path);

                            long prefixid;
                            if (string.Equals(prevprefix.Key, split.Key, StringComparison.Ordinal))
                                prefixid = prevprefix.Value;
                            else
                            {
                                prefixid = await database.GetOrCreatePathPrefix(split.Key, taskreader.ProgressToken);
                                prevprefix = new KeyValuePair<string, long>(split.Key, prefixid);
                            }

                            if (CHECKFILETIMEONLY || DISABLEFILETIMECHECK)
                            {
                                var tmp = await database.GetFileLastModifiedAsync(prefixid, split.Value, lastfilesetid, false, taskreader.ProgressToken);
                                await self.Output.WriteAsync(new FileEntry
                                {
                                    OldId = tmp.Item1,
                                    Entry = entry,
                                    PathPrefixID = prefixid,
                                    Filename = split.Value,
                                    Attributes = attributes,
                                    LastWrite = lastwrite,
                                    OldModified = tmp.Item2,
                                    LastFileSize = tmp.Item3,
                                    OldMetaHash = null,
                                    OldMetaSize = -1
                                });
                            }
                            else
                            {
                                var res = await database.GetFileEntryAsync(prefixid, split.Value, lastfilesetid, taskreader.ProgressToken);
                                await self.Output.WriteAsync(new FileEntry
                                {
                                    OldId = res == null ? -1 : res.id,
                                    Entry = entry,
                                    PathPrefixID = prefixid,
                                    Filename = split.Value,
                                    Attributes = attributes,
                                    LastWrite = lastwrite,
                                    OldModified = res == null ? new DateTime(0) : res.modified,
                                    LastFileSize = res == null ? -1 : res.filesize,
                                    OldMetaHash = res == null ? null : res.metahash,
                                    OldMetaSize = res == null ? -1 : res.metasize
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            if (ex.IsRetiredException())
                                continue;

                            LogExceptionHelper.LogCommonWarning(ex, FILELOGTAG, "ProcessingMetadataFailed", entry.Path);
                        }
                    }
                }

            });

        }

        /// <summary>
        /// Processes the metadata for the given path.
        /// </summary>
        /// <param name="entry">The source provider entry to process.</param>
        /// <param name="attributes">The file attributes of the entry.</param>
        /// <param name="lastwrite">The last write timestamp of the entry.</param>
        /// <param name="options">The options for the backup operation.</param>
        /// <param name="emptymetadata">An empty metadata object to use if no metadata is generated.</param>
        /// <param name="database">The backup database to use for storing metadata.</param>
        /// <param name="streamblockchannel">The channel to write stream blocks to.</param>
        /// <param name="cancellationToken">The cancellation token to use for the operation.</param>
        /// <returns><c>True</c> if the path should be submitted to more analysis, <c>false</c> if there is nothing else to do</returns>
        private static async Task<bool> ProcessMetadata(ISourceProviderEntry entry, FileAttributes attributes, DateTime lastwrite, Options options, IMetahash emptymetadata, BackupDatabase database, IWriteChannel<StreamBlock> streamblockchannel, CancellationToken cancellationToken)
        {
            if (entry.IsSymlink)
            {
                // Not all reparse points are symlinks.
                // For example, on Windows 10 Fall Creator's Update, the OneDrive folder (and all subfolders)
                // are reparse points, which allows the folder to hook into the OneDrive service and download things on-demand.
                // If we can't find a symlink target for the current path, we won't treat it as a symlink.
                string symlinkTarget = null;
                try
                {
                    symlinkTarget = entry.SymlinkTarget;
                }
                catch (Exception ex)
                {
                    Logging.Log.WriteExplicitMessage(FILELOGTAG, "SymlinkTargetReadFailure", ex, "Failed to read symlink target for path: {0}", entry.Path);
                }

                if (!string.IsNullOrWhiteSpace(symlinkTarget))
                {
                    if (options.SymlinkPolicy == Options.SymlinkStrategy.Ignore)
                    {
                        Logging.Log.WriteVerboseMessage(FILELOGTAG, "IgnoreSymlink", "Ignoring symlink {0}", entry.Path);
                        return false;
                    }

                    if (options.SymlinkPolicy == Options.SymlinkStrategy.Store)
                    {
                        var metadata = MetadataGenerator.GenerateMetadata(entry, attributes, options);

                        if (!metadata.ContainsKey("CoreSymlinkTarget"))
                            metadata["CoreSymlinkTarget"] = symlinkTarget;

                        var metahash = Utility.WrapMetadata(metadata, options);
                        await AddSymlinkToOutputAsync(entry.Path, DateTime.UtcNow, metahash, database, streamblockchannel, cancellationToken).ConfigureAwait(false);

                        Logging.Log.WriteVerboseMessage(FILELOGTAG, "StoreSymlink", "Stored symlink {0}", entry.Path);
                        // Don't process further
                        return false;
                    }
                }
                else
                {
                    Logging.Log.WriteVerboseMessage(FILELOGTAG, "FollowingEmptySymlink", "Treating empty symlink as regular path {0}", entry.Path);
                }
            }


            if ((attributes & FileAttributes.Directory) == FileAttributes.Directory)
            {
                IMetahash metahash;

                if (!options.SkipMetadata)
                {
                    metahash = Utility.WrapMetadata(MetadataGenerator.GenerateMetadata(entry, attributes, options), options);
                }
                else
                {
                    metahash = emptymetadata;
                }

                Logging.Log.WriteVerboseMessage(FILELOGTAG, "AddDirectory", "Adding directory {0}", entry.Path);
                await AddFolderToOutputAsync(entry.Path, lastwrite, metahash, database, streamblockchannel, cancellationToken).ConfigureAwait(false);
                return false;
            }

            // Regular file, keep going
            return true;
        }

        /// <summary>
        /// Adds metadata to output, and returns the metadataset ID
        /// </summary>
        /// <param name="path">The path for which metadata is processed.</param>
        /// <param name="meta">The metadata entry.</param>
        /// <param name="database">The database connection.</param>
        /// <param name="streamblockchannel">The channel to write streams to.</param>
        /// <param name="cancellationToken">The cancellation token to use.</param>
        /// <returns>A task that when awaited returns a tuple with the first value indicating if the metadata set was added, and the second value being the metadata ID.</returns>
        internal static async Task<(bool, long)> AddMetadataToOutputAsync(string path, IMetahash meta, BackupDatabase database, IWriteChannel<StreamBlock> streamblockchannel, CancellationToken cancellationToken)
        {
            StreamProcessResult res;
            using (var ms = new MemoryStream(meta.Blob))
                res = await StreamBlock.ProcessStream(streamblockchannel, path, ms, true, CompressionHint.Default);

            return await database.AddMetadatasetAsync(res.Streamhash, res.Streamlength, res.Blocksetid, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Adds a file to the output,
        /// </summary>
        /// <param name="filename">The name of the file to record</param>
        /// <param name="lastModified">The value of the lastModified timestamp</param>
        /// <param name="meta">The metadata to record</param>
        /// <param name="database">The database to use</param>
        /// <param name="streamblockchannel">The channel to write blocks to</param>
        /// <param name="cancellationToken">The cancellation token to use</param>
        /// <returns>A task that completes when the file has been added to the output.</returns>
        private static async Task AddFolderToOutputAsync(string filename, DateTime lastModified, IMetahash meta, BackupDatabase database, IWriteChannel<StreamBlock> streamblockchannel, CancellationToken cancellationToken)
        {
            var metadataid = await AddMetadataToOutputAsync(filename, meta, database, streamblockchannel, cancellationToken).ConfigureAwait(false);
            await database.AddDirectoryEntryAsync(filename, metadataid.Item2, lastModified, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Adds a file to the output,
        /// </summary>
        /// <param name="filename">The name of the file to record</param>
        /// <param name="lastModified">The value of the lastModified timestamp</param>
        /// <param name="meta">The metadata ti record</param>
        /// <param name="database">The database to use</param>
        /// <param name="streamblockchannel">The channel to write blocks to</param>
        /// <param name="cancellationToken">The cancellation token to use</param>
        /// <returns>A task that completes when the file has been added to the output.</returns>
        private static async Task AddSymlinkToOutputAsync(string filename, DateTime lastModified, IMetahash meta, BackupDatabase database, IWriteChannel<StreamBlock> streamblockchannel, CancellationToken cancellationToken)
        {
            var metadataid = await AddMetadataToOutputAsync(filename, meta, database, streamblockchannel, cancellationToken).ConfigureAwait(false);
            await database.AddSymlinkEntryAsync(filename, metadataid.Item2, lastModified, cancellationToken).ConfigureAwait(false);
        }

    }
}

