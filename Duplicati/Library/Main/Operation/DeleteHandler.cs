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
using System.Linq;
using System.Collections.Generic;
using Duplicati.Library.Interface;
using Duplicati.Library.Main.Database;
using Duplicati.Library.Utility;
using System.Threading;
using System.Threading.Tasks;

namespace Duplicati.Library.Main.Operation
{
    internal class DeleteHandler
    {
        /// <summary>
        /// The tag used for logging
        /// </summary>
        internal static readonly string LOGTAG = Logging.Log.LogTagFromType<DeleteHandler>();

        private readonly DeleteResults m_result;
        protected readonly Options m_options;

        public DeleteHandler(Options options, DeleteResults result)
        {
            m_options = options;
            m_result = result;
        }

        public async Task RunAsync(IBackendManager backendManager)
        {
            if (!System.IO.File.Exists(m_options.Dbpath))
                throw new UserInformationException(string.Format("Database file does not exist: {0}", m_options.Dbpath), "DatabaseFileMissing");

            using (var db = await LocalDeleteDatabase.CreateAsync(m_options.Dbpath, "Delete", m_options.SqlitePageCache))
            {
                Utility.UpdateOptionsFromDb(db, m_options);
                Utility.VerifyOptionsAndUpdateDatabase(db, m_options);

                await DoRunAsync(db, false, false, backendManager).ConfigureAwait(false);

                if (!m_options.Dryrun)
                    await db.Transaction.CommitAsync("ComitDelete", restart: false);
            }
        }

        public async Task DoRunAsync(LocalDeleteDatabase db, bool hasVerifiedBackend, bool forceCompact, IBackendManager backendManager)
        {
            if (!hasVerifiedBackend)
                await FilelistProcessor.VerifyRemoteList(backendManager, m_options, db, m_result.BackendWriter, latestVolumesOnly: true, verifyMode: FilelistProcessor.VerifyMode.VerifyStrict).ConfigureAwait(false);

            // We collapse the async enumerablo into a array to avoid multiple
            // enumerations (and thus multiple database queries)
            var filesets = await db.FilesetsWithBackupVersion().ToArrayAsync();
            List<IListResultFileset> versionsToDelete =
            [
                .. new SpecificVersionsRemover(m_options).GetFilesetsToDelete(filesets),
                .. new KeepTimeRemover(m_options).GetFilesetsToDelete(filesets),
                .. new RetentionPolicyRemover(m_options).GetFilesetsToDelete(filesets),
            ];

            // When determining the number of full versions to keep, we need to ignore the versions already marked for removal.
            versionsToDelete.AddRange(new KeepVersionsRemover(m_options).GetFilesetsToDelete(filesets.Except(versionsToDelete)));

            // In case multiple options are supplied, only consider distinct versions to delete.
            versionsToDelete = versionsToDelete.DistinctBy(x => x.Version).ToList();

            if (!m_options.AllowFullRemoval && filesets.Length == versionsToDelete.Count)
            {
                Logging.Log.WriteWarningMessage(LOGTAG, "PreventingLastFilesetRemoval", null, "Preventing removal of last fileset, use --{0} to allow removal ...", "allow-full-removal");
                versionsToDelete = versionsToDelete.OrderBy(x => x.Version).Skip(1).ToList();
            }

            if (versionsToDelete.Count == 0)
            {
                Logging.Log.WriteInformationMessage(LOGTAG, "NoFilesetsToDelete", "No remote filesets should be deleted");
            }
            else
            {
                Logging.Log.WriteInformationMessage(LOGTAG, "DeleteRemoteFileset", "Deleting {0} remote fileset(s) ...", versionsToDelete.Count);

                var lst = await db.DropFilesetsFromTable(versionsToDelete.Select(x => x.Time).ToArray()).ToArrayAsync();
                foreach (var f in lst)
                    await db.UpdateRemoteVolume(f.Key, RemoteVolumeState.Deleting, f.Value, null);

                if (!m_options.Dryrun)
                    await db.Transaction.CommitAsync("CommitBeforeDelete");

                foreach (var f in lst)
                {
                    if (!await m_result.TaskControl.ProgressRendevouz().ConfigureAwait(false))
                    {
                        await backendManager.WaitForEmptyAsync(db, m_result.TaskControl.ProgressToken).ConfigureAwait(false);
                        return;
                    }

                    if (!m_options.Dryrun)
                        await backendManager.DeleteAsync(f.Key, f.Value, false, m_result.TaskControl.ProgressToken).ConfigureAwait(false);
                    else
                        Logging.Log.WriteDryrunMessage(LOGTAG, "WouldDeleteRemoteFileset", "Would delete remote fileset: {0}", f.Key);
                }

                await backendManager.WaitForEmptyAsync(db, m_result.TaskControl.ProgressToken).ConfigureAwait(false);

                var count = lst.Length;
                if (!m_options.Dryrun)
                {
                    if (count == 0)
                        Logging.Log.WriteInformationMessage(LOGTAG, "DeleteResults", "No remote filesets were deleted");
                    else
                        Logging.Log.WriteInformationMessage(LOGTAG, "DeleteResults", "Deleted {0} remote fileset(s)", count);
                }
                else
                {

                    if (count == 0)
                        Logging.Log.WriteDryrunMessage(LOGTAG, "WouldDeleteResults", "No remote filesets would be deleted");
                    else
                        Logging.Log.WriteDryrunMessage(LOGTAG, "WouldDeleteResults", "{0} remote fileset(s) would be deleted", count);

                    if (count > 0 && m_options.Dryrun)
                        Logging.Log.WriteDryrunMessage(LOGTAG, "WouldDeleteHelp", "Remove --dry-run to actually delete files");
                }
            }

            if (!m_options.NoAutoCompact && (forceCompact || versionsToDelete.Count > 0))
            {
                m_result.CompactResults = new CompactResults(m_result);
                await new CompactHandler(m_options, (CompactResults)m_result.CompactResults)
                    .DoCompactAsync(db, true, backendManager).ConfigureAwait(false);
            }

            m_result.SetResults(versionsToDelete.Select(v => new Tuple<long, DateTime>(v.Version, v.Time)), m_options.Dryrun);
        }
    }

    public abstract class FilesetRemover
    {
        protected readonly Options Options;

        protected FilesetRemover(Options options)
        {
            this.Options = options;
        }

        public abstract IEnumerable<IListResultFileset> GetFilesetsToDelete(IEnumerable<IListResultFileset> filesets);
    }

    /// <summary>
    /// Remove versions specified by the --version option.
    /// </summary>
    public class SpecificVersionsRemover : FilesetRemover
    {
        public SpecificVersionsRemover(Options options) : base(options)
        {
        }

        public override IEnumerable<IListResultFileset> GetFilesetsToDelete(IEnumerable<IListResultFileset> filesets)
        {
            ISet<long> versionsToDelete = new HashSet<long>(this.Options.Version ?? new long[0]);
            return filesets.Where(x => versionsToDelete.Contains(x.Version));
        }
    }

    /// <summary>
    /// Keep backups that are newer than the date specified by the --keep-time option.
    /// If none of the retained versions are full backups, then continue to keep versions
    /// until we have a full backup.
    /// </summary>
    public class KeepTimeRemover : FilesetRemover
    {
        public KeepTimeRemover(Options options) : base(options)
        {
        }

        public override IEnumerable<IListResultFileset> GetFilesetsToDelete(IEnumerable<IListResultFileset> filesets)
        {
            IListResultFileset[] sortedFilesets = filesets.OrderByDescending(x => x.Time).ToArray();
            List<IListResultFileset> versionsToDelete = new List<IListResultFileset>();

            DateTime earliestTime = this.Options.KeepTime;
            if (earliestTime.Ticks > 0)
            {
                bool haveFullBackup = false;
                versionsToDelete.AddRange(sortedFilesets.SkipWhile(x =>
                {
                    bool keepBackup = (x.Time >= earliestTime) || !haveFullBackup;
                    haveFullBackup = haveFullBackup || (x.IsFullBackup == BackupType.FULL_BACKUP);
                    return keepBackup;
                }));
            }

            return versionsToDelete;
        }
    }

    /// <summary>
    /// Keep a number of recent full backups as specified by the --keep-versions option.
    /// Partial backups that are surrounded by full backups will also be removed.
    /// </summary>
    public class KeepVersionsRemover : FilesetRemover
    {
        public KeepVersionsRemover(Options options) : base(options)
        {
        }

        public override IEnumerable<IListResultFileset> GetFilesetsToDelete(IEnumerable<IListResultFileset> filesets)
        {
            IListResultFileset[] sortedFilesets = filesets.OrderByDescending(x => x.Time).ToArray();
            List<IListResultFileset> versionsToDelete = new List<IListResultFileset>();

            // Check how many full backups will be remaining after the previous steps
            // and remove oldest backups while there are still more backups than should be kept as specified via option
            int fullVersionsToKeep = this.Options.KeepVersions;
            if (fullVersionsToKeep > 0 && fullVersionsToKeep < sortedFilesets.Length)
            {
                int fullVersionsKept = 0;
                ISet<IListResultFileset> intermediatePartials = new HashSet<IListResultFileset>();

                // Enumerate the collection starting from the most recent full backup.
                foreach (IListResultFileset fileset in sortedFilesets.SkipWhile(x => x.IsFullBackup == BackupType.PARTIAL_BACKUP))
                {
                    if (fullVersionsKept >= fullVersionsToKeep)
                    {
                        // If we have enough full backups, delete all older backups.
                        versionsToDelete.Add(fileset);
                    }
                    else if (fileset.IsFullBackup == BackupType.FULL_BACKUP)
                    {
                        // We can delete partial backups that are surrounded by full backups.
                        versionsToDelete.AddRange(intermediatePartials);
                        intermediatePartials.Clear();
                        fullVersionsKept++;
                    }
                    else
                    {
                        intermediatePartials.Add(fileset);
                    }
                }
            }

            return versionsToDelete;
        }
    }

    /// <summary>
    /// Remove backups according to the --retention-policy option.
    /// Partial backups are not removed.
    /// </summary>
    public class RetentionPolicyRemover : FilesetRemover
    {
        private static readonly string LOGTAG_RETENTION = DeleteHandler.LOGTAG + ":RetentionPolicy";

        public RetentionPolicyRemover(Options options) : base(options)
        {
        }

        public override IEnumerable<IListResultFileset> GetFilesetsToDelete(IEnumerable<IListResultFileset> filesets)
        {
            IListResultFileset[] sortedFilesets = filesets.OrderByDescending(x => x.Time).ToArray();
            List<IListResultFileset> versionsToDelete = new List<IListResultFileset>();

            List<Options.RetentionPolicyValue> retentionPolicyOptionValues = this.Options.RetentionPolicy;
            if (retentionPolicyOptionValues.Count == 0 || sortedFilesets.Length == 0)
            {
                return versionsToDelete;
            }

            Logging.Log.WriteInformationMessage(LOGTAG_RETENTION, "StartCheck", "Start checking if backups can be removed");

            // Work with a copy to not modify the enumeration that the caller passed
            List<IListResultFileset> clonedBackupList = new List<IListResultFileset>(sortedFilesets);

            // Most recent backup usually should never get deleted in this process, so exclude it for now,
            // but keep a reference to potential delete it when allow-full-removal is set
            IListResultFileset mostRecentBackup = clonedBackupList.ElementAt(0);
            clonedBackupList.RemoveAt(0);
            bool deleteMostRecentBackup = this.Options.AllowFullRemoval;

            Logging.Log.WriteInformationMessage(LOGTAG_RETENTION, "FramesAndIntervals", "Time frames and intervals pairs: {0}", string.Join(", ", retentionPolicyOptionValues));
            Logging.Log.WriteInformationMessage(LOGTAG_RETENTION, "BackupList", "Backups to consider: {0}", string.Join(", ", clonedBackupList.Select(x => x.Time)));

            // Collect all potential backups in each time frame and thin out according to the specified interval,
            // starting with the oldest backup in that time frame.
            // The order in which the time frames values are checked has to be from the smallest to the largest.
            DateTime now = DateTime.Now;
            foreach (Options.RetentionPolicyValue singleRetentionPolicyOptionValue in retentionPolicyOptionValues.OrderBy(x => x.Timeframe))
            {
                // The timeframe in the retention policy option is only a timespan which has to be applied to the current DateTime to get the actual lower bound
                DateTime timeFrame = (singleRetentionPolicyOptionValue.IsUnlimtedTimeframe()) ? DateTime.MinValue : (now - singleRetentionPolicyOptionValue.Timeframe);

                Logging.Log.WriteProfilingMessage(LOGTAG_RETENTION, "NextTimeAndFrame", "Next time frame and interval pair: {0}", singleRetentionPolicyOptionValue);

                List<IListResultFileset> backupsInTimeFrame = new List<IListResultFileset>();
                while (clonedBackupList.Count > 0 && clonedBackupList[0].Time >= timeFrame)
                {
                    backupsInTimeFrame.Insert(0, clonedBackupList[0]); // Insert at beginning to reverse order, which is necessary for next step
                    clonedBackupList.RemoveAt(0); // remove from here to not handle the same backup in two time frames
                }

                Logging.Log.WriteProfilingMessage(LOGTAG_RETENTION, "BackupsInFrame", "Backups in this time frame: {0}", string.Join(", ", backupsInTimeFrame.Select(x => x.Time)));

                // Run through backups in this time frame
                IListResultFileset lastKept = null;
                foreach (IListResultFileset fileset in backupsInTimeFrame)
                {
                    bool isFullBackup = fileset.IsFullBackup == BackupType.FULL_BACKUP;

                    // Keep this backup if
                    // - no backup has yet been added to the time frame (keeps at least the oldest backup in a time frame)
                    // - difference between last added backup and this backup is bigger than the specified interval
                    if (lastKept == null || singleRetentionPolicyOptionValue.IsKeepAllVersions() || (fileset.Time - lastKept.Time) >= singleRetentionPolicyOptionValue.Interval)
                    {
                        Logging.Log.WriteProfilingMessage(LOGTAG_RETENTION, "KeepBackups", $"Keeping {(isFullBackup ? "" : "partial")} backup: {fileset.Time}", Logging.LogMessageType.Profiling);
                        if (isFullBackup)
                        {
                            lastKept = fileset;
                        }
                    }
                    else
                    {
                        if (isFullBackup)
                        {
                            Logging.Log.WriteProfilingMessage(LOGTAG_RETENTION, "DeletingBackups", "Deleting backup: {0}", fileset.Time);
                            versionsToDelete.Add(fileset);
                        }
                        else
                        {
                            Logging.Log.WriteProfilingMessage(LOGTAG_RETENTION, "KeepBackups", $"Keeping partial backup: {fileset.Time}", Logging.LogMessageType.Profiling);
                        }
                    }
                }

                // Check if most recent backup is outside of this time frame (meaning older/smaller)
                deleteMostRecentBackup &= (mostRecentBackup.Time < timeFrame);
            }

            // Delete all remaining backups
            versionsToDelete.AddRange(clonedBackupList);
            Logging.Log.WriteInformationMessage(LOGTAG_RETENTION, "BackupsToDelete", "Backups outside of all time frames and thus getting deleted: {0}", string.Join(", ", clonedBackupList.Select(x => x.Time)));

            // Delete most recent backup if allow-full-removal is set and the most current backup is outside of any time frame
            if (deleteMostRecentBackup)
            {
                versionsToDelete.Add(mostRecentBackup);
                Logging.Log.WriteInformationMessage(LOGTAG_RETENTION, "DeleteMostRecent", "Deleting most recent backup: {0}", mostRecentBackup.Time);
            }

            Logging.Log.WriteInformationMessage(LOGTAG_RETENTION, "AllBackupsToDelete", "All backups to delete: {0}", string.Join(", ", versionsToDelete.Select(x => x.Time).OrderByDescending(x => x)));

            return versionsToDelete;
        }
    }
}

