﻿namespace Quickbird.Services
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;
    using System.Threading.Tasks;
    using Windows.UI.Xaml;
    using DbStructure;
    using DbStructure.User;
    using Internet;
    using Microsoft.EntityFrameworkCore;
    using Models;
    using Newtonsoft.Json;
    using Quickbird.Util;


    /// <summary>Methods to access and update the database. Methods that modify the database are queued to
    /// make sure they do not interfere with one another. </summary>
    public partial class DataService
    {
        /// <summary>The Url of the web api that is used to fetch data.</summary>
        public const string ApiUrl = "https://greenhouseapi.azurewebsites.net/api";

        /// <summary>The maximum number of days to download at a time.</summary>
        private const int MaxDaysDl = 15;

        /// <summary>An complete task that can be have ContinueWith() called on it. Used to queue database
        /// tasks to make sure one completes before another starts.</summary>
        private Task _lastTask = Task.CompletedTask;

        private DataService() { }

        /// <summary>Singleton instance accessor.</summary>
        public static DataService Instance { get; } = new DataService();

        /// <summary>Requires UI thread. Syncs databse data with the server.</summary>
        /// <remarks>This method runs on the UI thread, the queued methods need it, they hand off work to the
        /// threadpool as appropriate.</remarks>
        public async Task SyncWithServerAsync()
        {
            // Sharing a db context allows the use of caching within the context while still being safer than a leaky 
            //  global context.
            using (var db = new MainDbContext())
            {
                var updateErrors = await GetRequestUpdateAsyncQueued(db);
                if (updateErrors?.Any() ?? false)
                {
                    LoggingService.LogInfo(string.Join(",", updateErrors), Windows.Foundation.Diagnostics.LoggingLevel.Error );
                    return;
                }

                var updateHistErrors = await GetRequestSensorHistoryAsyncQueued(db);
                if (updateHistErrors?.Any() ?? false)
                {
                    LoggingService.LogInfo(string.Join(",", updateHistErrors), Windows.Foundation.Diagnostics.LoggingLevel.Error);
                    return;
                }

                var postErrors = await PostRequestUpdatesAsyncQueued(db);
                if (postErrors?.Any() ?? false)
                {
                    LoggingService.LogInfo(string.Join(",", postErrors), Windows.Foundation.Diagnostics.LoggingLevel.Error);
                    return;
                }

                var postHistErrors = await PostRequestHistoryAsyncQueued(db);
                if (postHistErrors?.Any() ?? false)
                {
                    LoggingService.LogInfo(string.Join(",", postHistErrors), Windows.Foundation.Diagnostics.LoggingLevel.Error);
                }
            }
        }

        /// <summary>The method should be executed on the UI thread, which means it should be called before any
        /// awaits, before the the method returns.</summary>
        private Task<T> AttachContinuationsAndSwapLastTask<T>(Func<T> workForNextTask)
        {
            var contTask = _lastTask.ContinueWith(_ => workForNextTask());
            _lastTask = contTask;
            ((App)Application.Current).AddSessionTask(contTask);
            return contTask;
        }

        /// <summary>Updates sensor history from server. Queued to run after existing database and server
        /// operations.</summary>
        /// <param name="db"></param>
        /// <returns>Errors, null on succes.</returns>
        private async Task<string> GetRequestSensorHistoryAsyncQueued(MainDbContext db)
        {
            var cont = AttachContinuationsAndSwapLastTask(() => Task.Run(() => GetRequestSensorHistoryAsync(db)));
            var updatesFromServerAsync = await await cont.ConfigureAwait(false);
            await BroadcasterService.Instance.TablesChanged.Invoke(null);
            return updatesFromServerAsync;
        }

        /// <summary>Updates the database from the cloud server.</summary>
        /// <returns>A compilation of errors.</returns>
        private async Task<List<string>> GetRequestUpdateAsyncQueued(MainDbContext db)
        {
            var cont = AttachContinuationsAndSwapLastTask(() => Task.Run(() => GetRequestUpdateAsync(db)));
            var updatesFromServerAsync = await await cont.ConfigureAwait(false);
            await BroadcasterService.Instance.TablesChanged.Invoke(null);
            return updatesFromServerAsync;
        }

        /// <summary>Only supports tables that derive from BaseEntity and Croptype.</summary>
        /// <param name="table">The DBSet object for the table.</param>
        /// <param name="tableName">The name of the table in the API .</param>
        /// <param name="lastPost">The last time the table was synced.</param>
        /// <param name="creds">Authentication credentials.</param>
        /// <returns>Null on success otherwise an error message.</returns>
        private static async Task<string> PostRequestTableWhereUpdatedAsync(IQueryable<BaseEntity> table,
            string tableName, DateTimeOffset lastPost, Creds creds)
        {
            var edited = table.AsNoTracking().Where(t => t.UpdatedAt > lastPost).ToList();

            if (!edited.Any()) return null;

            var data = JsonConvert.SerializeObject(edited, Formatting.None);
            var req = await Request.PostTable(ApiUrl, tableName, data, creds).ConfigureAwait(false);
            return req;
        }

        private async Task<List<string>> PostRequestUpdatesAsyncQueued(MainDbContext db)
        {
            var cont = AttachContinuationsAndSwapLastTask(() => Task.Run(() => PostRequestUpdateAsync(db)));
            return await await cont.ConfigureAwait(false);
        }

        private async Task<string> PostRequestHistoryAsyncQueued(MainDbContext db)
        {
            var cont = AttachContinuationsAndSwapLastTask(() => Task.Run(() => PostRequestHistoryAsync(db)));
            return await await cont.ConfigureAwait(false);
        }

        /// <summary>Figures out the real type of the table entitiy, performs checks for existing items and
        /// merges data where required.</summary>
        /// <typeparam name="TPoco">The POCO type of the entity.</typeparam>
        /// <param name="updatesFromServer">The data recieved from the server.</param>
        /// <param name="dbSet">The actual databse table.</param>
        /// <returns>Awaitable, the local database queries are done async.</returns>
        private void AddOrModify<TPoco>(List<TPoco> updatesFromServer, DbSet<TPoco> dbSet) where TPoco : class
        {
            var pocoType = typeof(TPoco);
            foreach (var remote in updatesFromServer)
            {
                TPoco local = null;
                if (pocoType.GetInterfaces().Contains(typeof(IHasId)))
                {
                    local =
                        dbSet.AsNoTracking().Select(a => a).FirstOrDefault(d => ((IHasId) d).ID == ((IHasId) remote).ID);
                }
                else if (pocoType.GetInterfaces().Contains(typeof(IHasGuid)))
                {
                    local =
                        dbSet.AsNoTracking()
                            .Select(a => a)
                            .FirstOrDefault(d => ((IHasGuid) d).ID == ((IHasGuid) remote).ID);
                }
                else if (pocoType == typeof(CropType))
                {
                    var x = remote as CropType;
                    local = dbSet.OfType<CropType>().AsNoTracking().FirstOrDefault(d => d.Name == x.Name) as TPoco;
                }

                //Whatever it is, jsut add record to the DB 
                if (local == null)
                {
                    dbSet.Add(remote);
                }
                else
                {
                    //User Tables
                    if (remote is BaseEntity && local is BaseEntity)
                    {
                        // These types allow local changes. Check date and don't overwrite unless the server has changed.
                        var remoteVersion = remote as BaseEntity;
                        var localVersion = local as BaseEntity;

                        if (remoteVersion.UpdatedAt > localVersion.UpdatedAt)
                        {
                            // Overwrite local version, with the server's changes.
                            dbSet.Update(remote);
                            //await Messenger.Instance.UserTablesChanged.Invoke("update");
                        }
                    }
                    //RED - Global read-only tables
                    else
                    {
                        // Simply take the changes from the server, there are no valid local changes.
                        dbSet.Update(remote);
                        //await Messenger.Instance.HardwareTableChanged.Invoke("new");
                    }
                }
            }
        }






        /// <summary>Downloads derserialzes and add/merges a table.</summary>
        /// <typeparam name="TPoco">The POCO type of the table.</typeparam>
        /// <param name="tableName">Name of the table to request.</param>
        /// <param name="dbTable">The actual POCO table.</param>
        /// <param name="cred">Credentials to be used to authenticate with the server. Only required for some
        /// types.</param>
        /// <returns>Null on success, otherwise an error message.</returns>
        private async Task<string> GetReqDeserMergeTable<TPoco>(string tableName, DbSet<TPoco> dbTable,
            Creds cred = null) where TPoco : class
        {
            // Any exeption raised in these methods result in abort exeption with an error message for debug.
            try
            {
                // Step 1: Request
                var response = await GetRequestTableThrowOnErrorAsync(tableName, cred);

                // Step 2: Deserialise
                var updatesFromServer = await DeserializeTableThrowOnErrrorAsync<TPoco>(tableName, response);
                LoggingService.LogInfo($"Deserialised {updatesFromServer.Count} for {tableName}", Windows.Foundation.Diagnostics.LoggingLevel.Information);

                // Step 3: Merge
                // Get the DbSet that this request should be inserted into.
                AddOrModify(updatesFromServer, dbTable);
            }
            catch (Exception ex)
            {
                return ex.ToString();
            }

            return null;
        }



        /// <summary>Updates sensor history from server.</summary>
        /// <returns>Errors, null on succes. Some data may have been successfully saved alongside errors.</returns>
        private static async Task<string> GetRequestSensorHistoryAsync(MainDbContext db)
        {
            var devices = db.Devices.AsNoTracking().Include(d => d.Sensors).ToList();
            var table = nameof(db.SensorsHistory);
            var errors = "";

            //Query local sensors and locations. Mostly used for debug. 
            var LocalSensors = db.Sensors.ToList();
            var LocalLocation = db.Locations.ToList();

            // Download all the data for each device in turn.
            foreach (var device in devices)
            {
                // Find the newest received item so dowloading can resume after it.
                // There will be items for multiple sensors with the same time, it diesn't matter which one is used.
                // It will be updated after each item is added, much cheaper than re-querying.
                SensorHistory latestDownloadedBlock =
                    db.SensorsHistory.AsNoTracking()
                        .Include(sh => sh.Sensor)
                        .Where(sh => sh.Sensor.DeviceID == device.ID)
                        // Never uploaded days may be much newer than items that have not yet been downloaded.
                        .Where(sh => sh.UploadedAt != default(DateTimeOffset)) //Ignore never uploaded days.
                        .OrderByDescending(sh => sh.TimeStamp).FirstOrDefault(); //Don't use MaxBy, it wont EF query.

                DateTimeOffset mostRecentDownloadedTimestamp;
                if (null == latestDownloadedBlock)
                {
                    // Data has never been downloaded for this device, so start downloading for time 0.
                    mostRecentDownloadedTimestamp = default(DateTimeOffset);
                }
                else
                {
                    //The timestamp is always the end of the day so look inside to see what time the data actually ends.

                    // Its pulled out of the database as raw so deserialise to access the data.
                    latestDownloadedBlock.DeserialiseData();
                    if (latestDownloadedBlock.Data.Any())
                    {
                        mostRecentDownloadedTimestamp = latestDownloadedBlock.Data.Max(entry => entry.TimeStamp);
                    }
                    else
                    {
                        LoggingService.LogInfo(
                            $"Found a history with no entries: {device.Name}, {latestDownloadedBlock.TimeStamp}", Windows.Foundation.Diagnostics.LoggingLevel.Error);

                        // This is a broken situation, but it should be  fine if we continue from the start of the day.
                        mostRecentDownloadedTimestamp = latestDownloadedBlock.TimeStamp - TimeSpan.FromDays(1);
                    }
                }

               
                  
                // This loop will keep requesting data untill the server gives no more.
                bool itemsReceived = false; 
                var blocksToUpdate = new List<SensorHistory>();  
                do
                {
                    var cred = Creds.FromUserIdAndToken(SettingsService.Instance.CredUserId, SettingsService.Instance.CredToken);
                    // Although any time far in the past should work, using 0 makes intent clear in debugging.
                    var unixTimeSeconds = mostRecentDownloadedTimestamp == default(DateTimeOffset)
                        ? 0
                        : mostRecentDownloadedTimestamp.ToUnixTimeSeconds();

                    List<SensorHistory> remoteBlocks;
                    try
                    {
                        // Download with get request.
                        var raw =
                            await
                                GetRequestTableThrowOnErrorAsync($"{table}/{device.ID}/{unixTimeSeconds}/{MaxDaysDl}",
                                    cred).ConfigureAwait(false);
                        // Deserialise download to POCO object.
                        remoteBlocks =
                            await DeserializeTableThrowOnErrrorAsync<SensorHistory>(table, raw).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // Something went wrong, try moving onto next device.
                        var message = $"GetRequest or deserialise failed for {device.Name}: {ex}";
                        errors += message + Environment.NewLine;
                        LoggingService.LogInfo(message, Windows.Foundation.Diagnostics.LoggingLevel.Error);
                        break;
                    }

                    LoggingService.LogInfo($"{remoteBlocks.Count} dl for {device.Name}", Windows.Foundation.Diagnostics.LoggingLevel.Information);

                    if (remoteBlocks.Any())
                    {

                        var lastDayOfThisDownloadSet = DateTimeOffset.MinValue;
                        foreach (var remoteBlock in remoteBlocks)
                        {
                            LoggingService.LogInfo(
                                $"[{mostRecentDownloadedTimestamp}]{remoteBlock.TimeStamp}#{remoteBlock.UploadedAt}#{device.Name}#{remoteBlock.SensorID}",
                                Windows.Foundation.Diagnostics.LoggingLevel.Verbose);

                            // See if this is a new object.
                            var localBlock =
                                db.SensorsHistory
                                    .FirstOrDefault(
                                        sh => sh.SensorID == remoteBlock.SensorID &&
                                        sh.TimeStamp.Date == remoteBlock.TimeStamp.Date);
                            // Make sure that its not an object tracked from a previous loop.
                            // This can happen because the end of the previous day will always get sliced with no data.
                            // That end slice isn't perfect but is makes sure all the data was taken.
                            if (localBlock == null)
                            {
                                localBlock =
                                    blocksToUpdate.FirstOrDefault(
                                        sh => sh.SensorID == remoteBlock.SensorID && sh.TimeStamp.Date == remoteBlock.TimeStamp.Date);
                                if (localBlock != null)
                                {
                                    // Detach the existing object because it will be merged and replaced.
                                    var entityEntry = db.Entry(localBlock);
                                    entityEntry.State = EntityState.Detached;
                                    blocksToUpdate.Remove(localBlock);
                                }
                            }
                            if (localBlock == null)
                            {
                                // The json deserialiser deserialises json to the .Data property.
                                // The data has to be serialised into raw-data-blobs before saving to the databse.
                              /*  var linkedSensor = LocalSensors.FirstOrDefault(sen => sen.ID == remoteBlock.SensorID);
                                var linkedLocation = LocalLocation.FirstOrDefault(loc => loc.ID == remoteBlock.LocationID);
                                var allDevices = db.Devices.ToList(); 

                                var conflictBlocks =
                                db.SensorsHistory
                                .Include(sh => sh.Location)
                                    .Where(
                                        sh => sh.SensorID == remoteBlock.SensorID &&
                                        sh.TimeStamp.Date == remoteBlock.TimeStamp.Date).ToList(); 

                                //var ConflictLocation = db.Locations.FirstOrDefault(loc => loc.ID == conflictBlock.LocationID); */

                                remoteBlock.SerialiseData();
                                blocksToUpdate.Add(db.Add(remoteBlock).Entity);
                            }
                            else
                            {
                                // The data is pulled from the database as serialised raw data blobs.
                                localBlock.DeserialiseData();
                                localBlock.LocationID = remoteBlock.LocationID;
                                var linkedLocation = LocalLocation.FirstOrDefault(loc => loc.ID == remoteBlock.LocationID);

                                // The merged object merges using the deserialised Data property.
                                var merged = SensorHistory.Merge(localBlock, remoteBlock);
                                // Data has to be serialised again into raw data blobs for the database.
                                merged.SerialiseData();

                                localBlock.RawData = merged.RawData; 
                                blocksToUpdate.Add(localBlock);
                            }

                            // This day was just downloaded so it must be completed
                            if (remoteBlock.TimeStamp > lastDayOfThisDownloadSet)
                                lastDayOfThisDownloadSet = remoteBlock.TimeStamp;
                        }
                        // The GET request allways gets days completed to the end (start may be missing but not the end)
                        mostRecentDownloadedTimestamp = lastDayOfThisDownloadSet;
                        itemsReceived = true;
                    }
                    else
                    {
                        itemsReceived = false;
                    }
                } while (itemsReceived);

               // */
                // Save the data for this device.
                await Task.Run(() => db.SaveChanges()).ConfigureAwait(false);

                foreach (var entity in blocksToUpdate)
                {
                    var entry = db.Entry(entity);
                    if (entry.State != EntityState.Detached)
                    {
                        entry.State = EntityState.Detached;
                    }
                }

                // Move onto next device.
            }

            return string.IsNullOrWhiteSpace(errors) ? null : errors;
        }

        private async Task<List<string>> GetRequestUpdateAsync(MainDbContext db)
        {
            var settings = SettingsService.Instance;
            var creds = Creds.FromUserIdAndToken(settings.CredUserId, settings.CredToken);
            var now = DateTimeOffset.Now;

            var res = new List<string>();


            // Setting configure await to false allows all of this method to be run on the threadpool.
            // Without setting it false the continuation would be posted onto the SynchronisationContext, which is the UI.
            res.Add(await GetReqDeserMergeTable(nameof(db.Parameters), db.Parameters).ConfigureAwait(false));
            res.Add(await GetReqDeserMergeTable(nameof(db.Placements), db.Placements).ConfigureAwait(false));
            res.Add(await GetReqDeserMergeTable(nameof(db.Subsystems), db.Subsystems).ConfigureAwait(false));
            res.Add(await GetReqDeserMergeTable(nameof(db.RelayTypes), db.RelayTypes).ConfigureAwait(false));
            res.Add(await GetReqDeserMergeTable(nameof(db.SensorTypes), db.SensorTypes).ConfigureAwait(false));

            if (res.Any(r => r != null))
            {
                return res.Where(r => r != null).ToList();
            }
            db.SaveChanges();

            // Editable types that must be merged:

            res.Add(await GetReqDeserMergeTable(nameof(db.People), db.People, creds).ConfigureAwait(false));
            // Crop type is the only mergable that is no-auth.
            res.Add(await GetReqDeserMergeTable(nameof(db.CropTypes), db.CropTypes).ConfigureAwait(false));
            res.Add(await GetReqDeserMergeTable(nameof(db.Locations), db.Locations, creds).ConfigureAwait(false));
            res.Add(await GetReqDeserMergeTable(nameof(db.CropCycles), db.CropCycles, creds).ConfigureAwait(false));
            res.Add(await GetReqDeserMergeTable(nameof(db.Devices), db.Devices, creds).ConfigureAwait(false));
            res.Add(await GetReqDeserMergeTable(nameof(db.Relays), db.Relays, creds).ConfigureAwait(false));
            res.Add(await GetReqDeserMergeTable(nameof(db.Sensors), db.Sensors, creds).ConfigureAwait(false));

            if (res.Any(r => r != null))
            {
                return res.Where(r => r != null).ToList();
            }
            db.SaveChanges();


            res = res.Where(r => r != null).ToList();

            if (!res.Any())
            {
                settings.LastSuccessfulGeneralDbGet = now;
            }

            return res;
        }

        /// <summary>Posts all new history items since the last time data was posted.</summary>
        /// <returns></returns>
        private static async Task<string> PostRequestHistoryAsync(MainDbContext db)
        {
            var creds = Creds.FromUserIdAndToken(SettingsService.Instance.CredUserId, SettingsService.Instance.CredToken);
            var tableName = nameof(db.SensorsHistory);

            if (!db.SensorsHistory.Any()) return null;

            var uploadedAtWillBe = DateTimeOffset.Now;

            // This is a list of never uploaded histories (half uploaded locally updated histories will be exluded).
            // It should be noted that it is impossible to have a never-uploaded history older than a half uploaded one.
            //TRACKING
            var uploadSet = db.SensorsHistory.AsTracking().Where(s => s.UploadedAt == default(DateTimeOffset)).ToList();
            var uploadQueue = new Queue<SensorHistory>(uploadSet);

            while (uploadQueue.Count > 0)
            {
                // Collect a batch of a sensible size.
                var batch = new List<SensorHistory>();
                const int maxUploadBatch = 30;
                while (batch.Count < maxUploadBatch && uploadQueue.Any())
                {
                    var item = uploadQueue.Dequeue();
                    // Histories come out of the database as raw blobs, serialise to populate the Data property.
                    // The json converter needs the Data property.
                    item.DeserialiseData();
                    batch.Add(item);
                    // This will only be saved if the whole upload is successful.
                    item.UploadedAt = uploadedAtWillBe;

                    // This next line combined with AsNoTracking would be efficient, but i dont trust EFCore.
                    db.Entry(item).Property(nameof(item.UploadedAt)).IsModified = true;
                }

                // Prepare and upload collection of histories.
                var json = JsonConvert.SerializeObject(batch);
                var result = await Request.PostTable(ApiUrl, tableName, json, creds).ConfigureAwait(false);
                if (result != null)
                {
                    //abort, non-null responses are descriptions of errors 
                    return result;
                }
            }

            // Upload success, we can save the changes to UploadedAt
            await Task.Run(() => db.SaveChanges()).ConfigureAwait(true);

            // Untrack the items so they cant clash with the next part.
            foreach (var tracked in uploadSet)
            {
                db.Entry(tracked).State = EntityState.Detached;
            }

            // Now we need to upload anything that could have been uploaded after having its UploadedAt set.
            // We can recognise these because the UploadedAt will be newer than the timestamp.

            //TRACKED.
            var uploadedButMayBeChangedSet =
                db.SensorsHistory.AsTracking().Where(sh => sh.TimeStamp > sh.UploadedAt).ToList();

            var haveChanged = new List<SensorHistory>();

            foreach (var uploadedButMayBeChanged in uploadedButMayBeChangedSet)
            {
                // Detect local modifications by looking for datapoint timestamps newer than UploadedAt.
                uploadedButMayBeChanged.DeserialiseData();
                if (uploadedButMayBeChanged.Data.Any(d => d.TimeStamp > uploadedButMayBeChanged.UploadedAt))
                {
                    var updatedTime = DateTimeOffset.Now;
                    // Get the new part by clicing after the UploadAt date.
                    var hasChanged = uploadedButMayBeChanged.Slice(uploadedButMayBeChanged.UploadedAt);

                    haveChanged.Add(hasChanged);

                    uploadedButMayBeChanged.UploadedAt = updatedTime;
                }
            }


            var jsonOfLocallyChanged = JsonConvert.SerializeObject(haveChanged);
            LoggingService.LogInfo($"Uploading {haveChanged.Count} histories.", Windows.Foundation.Diagnostics.LoggingLevel.Verbose);
            var uploadLocallyChangedResult = await Request.PostTable(ApiUrl, tableName, jsonOfLocallyChanged, creds);
            if (uploadLocallyChangedResult != null)
            {
                //abort
                return uploadLocallyChangedResult;
            }

            // Upload success, we can save the changes to UploadedAt
            await Task.Run(() => db.SaveChanges()).ConfigureAwait(true);

            // Untracking the items isn't necessary because this is the last part of Sync, context to be disposed.
            // However its much safer to do so incase someone updates Sync with new stuff.
            foreach (var tracked in uploadedButMayBeChangedSet)
            {
                db.Entry(tracked).State = EntityState.Detached;
            }
            // Return success.

            return null;
        }

        /// <summary>Posts changes saved in the local DB (excluding histories) to the server. Only Items with
        /// UpdatedAt or CreatedAt changed since the last post are posted.</summary>
        private static async Task<List<string>> PostRequestUpdateAsync(MainDbContext db)
        {
            var creds = Creds.FromUserIdAndToken(SettingsService.Instance.CredUserId, SettingsService.Instance.CredToken);

            var lastDatabasePost = SettingsService.Instance.LastSuccessfulGeneralDbPost;
            var postTime = DateTimeOffset.Now;

            var responses = new List<string>();

            // Simple tables that change:
            // CropCycle, Devices.

            responses.Add(
                await
                    PostRequestTableWhereUpdatedAsync(db.Locations, nameof(db.Locations), lastDatabasePost, creds)
                        .ConfigureAwait(false));

            // CropTypes is unique:
            var changedCropTypes = db.CropTypes.Where(c => c.CreatedAt > lastDatabasePost);

            if (changedCropTypes.Any())
            {
                var cropTypeData = JsonConvert.SerializeObject(changedCropTypes);
                responses.Add(
                    await Request.PostTable(ApiUrl, nameof(db.CropTypes), cropTypeData, creds).ConfigureAwait(false));
            }

            responses.Add(
                await
                    PostRequestTableWhereUpdatedAsync(db.CropCycles, nameof(db.CropCycles), lastDatabasePost, creds)
                        .ConfigureAwait(false));
            responses.Add(
                await
                    PostRequestTableWhereUpdatedAsync(db.Devices, nameof(db.Devices), lastDatabasePost, creds)
                        .ConfigureAwait(false));
            responses.Add(
                await
                    PostRequestTableWhereUpdatedAsync(db.Sensors, nameof(db.Sensors), lastDatabasePost, creds)
                        .ConfigureAwait(false));
            responses.Add(
                await
                    PostRequestTableWhereUpdatedAsync(db.Relays, nameof(db.Relays), lastDatabasePost, creds)
                        .ConfigureAwait(false));


            var errors = responses.Where(r => r != null).ToList();
            if (!errors.Any()) SettingsService.Instance.LastSuccessfulGeneralDbPost = postTime;
            return errors;
        }
    }
}
