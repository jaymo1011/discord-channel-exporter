using Discord;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.IO.Compression;
using System.Threading;
using Discord.Commands;
using Newtonsoft.Json;

namespace DiscordBot
{
    public class ChannelBackupService
    {
        private const int MessageRequestLimit = 100;
        private const long DiscordMaxFileSize = 8000000; // For bots it's actually 8MB not 50MB :(
        private const int MaxConcurrentDownloads = 5;
        private const int SemaphoreCheckTime = 500;
        private const string DefaultChannelCachePath = "channelcache/";
        private const string DefaultExportPath = "exports/";

        // Cool Regex but it ended up being useless
        //static Regex AttachmentURLRegex = new Regex(@"^<?https?://cdn\.discordapp\.com/attachments/(?<channelSnowflake>\d{18})/(?<attachmentSnowflake>\d{18})/(?<fileName>.*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly HttpClient httpClient;
        private readonly DiscordSocketClient discordClient;
        private readonly DirectoryInfo ChannelCacheDir;
        private readonly DirectoryInfo ContentCacheDir;
        private readonly DirectoryInfo ExportsDir;
        private readonly JsonSerializer Serializer;

        public ChannelBackupService(HttpClient _http, DiscordSocketClient _discord)
        {
            httpClient = _http;
            discordClient = _discord;
            ChannelCacheDir = EnsureDirectory(Program.Configuration["channel-cache-dir"] ?? DefaultChannelCachePath);
            ContentCacheDir = EnsureDirectory(Path.Combine(ChannelCacheDir.FullName, "content/"));
            ExportsDir = EnsureDirectory(Program.Configuration["channel-export-dir"] ?? DefaultExportPath);
            Serializer = new JsonSerializer
            {
                NullValueHandling = NullValueHandling.Ignore,
                DefaultValueHandling = DefaultValueHandling.Ignore
            };
        }

        private static DirectoryInfo EnsureDirectory(string relativePath)
        {
            var directory = new DirectoryInfo(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), relativePath));

            if (!directory.Exists)
                directory.Create();

            return directory;
        }

        /*
        Taking a backup of a channel works in two stages, first, there is a backup/cache of the content which gets updated over time
        and is ensured to be up-to-date before an export to the user takes place.

        Next is the export which is a snapshot of the data cache at the current point in time.
        An export can contain multiple channels which saves on content space where the same attachment is posted across multiple channels.
        An export is also losslessly stripped and minified.
        */

        public struct ChannelCache
        {
            [JsonProperty("channel-snowflake", Order = 0)]
            public ulong Channel;

            [JsonProperty("newest-snowflake", Order = 1)]
            public ulong NewestSnowflake;

            [JsonProperty("authors", Order = 2)]
            public Dictionary<ulong, string> Authors;

            [JsonProperty("attachments", Order = 3)]
            public Dictionary<ulong, string> AttachmentCache;

            [JsonProperty("messages", Order = 4)]
            public Dictionary<ulong, ChannelCacheMessage> Messages;

            public ChannelCache(ulong channel, ulong anchor = 1, Dictionary<ulong, string> authors = null, Dictionary<ulong, string> attachments = null, Dictionary<ulong, ChannelCacheMessage> messages = null)
            {
                Channel = channel;
                NewestSnowflake = anchor;
                AttachmentCache = attachments ?? new Dictionary<ulong, string>();
                Authors = authors ?? new Dictionary<ulong, string>();
                Messages = messages ?? new Dictionary<ulong, ChannelCacheMessage>();
            }
        }

        public struct ChannelCacheMessage
        {
            [JsonProperty("author", Order = 0)]
            public ulong Author;

            [JsonProperty("content", Order = 1)]
            public string Content;

            [JsonProperty("attachments", Order = 2)]
            public IEnumerable<string> AttachmentUrls;

            [JsonProperty("embeds", Order = 3)]
            public IEnumerable<StrippedEmbed> Embeds;

            [JsonProperty("pinned", Order = 4)]
            public bool? Pinned;

            public ChannelCacheMessage(string content, ulong author, IEnumerable<string> attachments = null, IEnumerable<StrippedEmbed> embeds = null, bool pinned = false)
            {
                Content = content;
                Author = author;
                AttachmentUrls = attachments;
                Embeds = embeds;
                Pinned = null;
                if (pinned)
                    Pinned = true;
            }
        }

        public class StrippedEmbed : IEmbed
        {
            public string Url { get; set; }
            public string Title { get; set; }
            public string Description { get; set; }
            public EmbedType Type { get; set; }
            public DateTimeOffset? Timestamp { get; set; }
            public Color? Color { get; set; }
            public EmbedImage? Image { get; set; }
            public EmbedVideo? Video { get; set; }
            public EmbedAuthor? Author { get; set; }
            public EmbedFooter? Footer { get; set; }
            public EmbedProvider? Provider { get; set; }
            public EmbedThumbnail? Thumbnail { get; set; }
            public ImmutableArray<EmbedField> Fields { get; set; }

            // It complained trying to cast an Embed to StrippedEmbed so now it can't!
            public static implicit operator StrippedEmbed(Embed e)
                => new StrippedEmbed
                    {
                        Author = e.Author,
                        Color = e.Color,
                        Description = e.Description,
                        Fields = e.Fields,
                        Footer = e.Footer,
                        Image = e.Image,
                        Provider = e.Provider,
                        Thumbnail = e.Thumbnail,
                        Timestamp = e.Timestamp,
                        Title = e.Title,
                        Type = e.Type,
                        Url = e.Url,
                        Video = e.Video
                    };
        }

        private string GetChannelCachePath(ulong channelId)
            => Path.Combine(ChannelCacheDir.FullName, $"{channelId}.json");

        private ChannelCache GetChannelCache(ulong channelId)
        {
            // Attempt to get an existing channel cache
            var fileInfo = new FileInfo(GetChannelCachePath(channelId));
            if (fileInfo.Exists && fileInfo.Length > 0)
            {
                using StreamReader file = File.OpenText(fileInfo.FullName);
                if (Serializer.Deserialize(file, typeof(ChannelCache)) is ChannelCache cache)
                    return cache;
            }

            return new ChannelCache(channelId);
        }

        private void SaveChannelCache(ChannelCache newCache)
            => SaveChannelCache(newCache.Channel, newCache);

        private void SaveChannelCache(ulong channelId, ChannelCache newCache)
        {
            using StreamWriter file = File.CreateText(GetChannelCachePath(channelId));

            // In-case there's an exception attempting to write the file out, we only want to flush (using will do this for us) when we're fully done serialising.
            file.AutoFlush = false;

            Serializer.Serialize(file, newCache);
        }

        public async Task UpdateChannelCache(ISocketMessageChannel channel)
        {
            // For reporting purposes, we'll make a stopwatch and some counters
            var timer = new Stopwatch();
            timer.Start();
            var messagesProcessed = 0;

            // First, we need to get the current channel cache
            var cache = GetChannelCache(channel.Id);

            Console.WriteLine($"Starting message cache update at {DateTime.UtcNow}");

            // Next, attempt to fetch messages
            bool requestAdditionalMessages = true;
            while (requestAdditionalMessages)
            {
                var startTimeForBatch = timer.Elapsed;
                Console.WriteLine($"Starting new message batch at {DateTime.UtcNow}");

                // Fetch messages from discord
                var messages = await channel.GetMessagesAsync(cache.NewestSnowflake, Direction.After, limit: MessageRequestLimit, mode: CacheMode.AllowDownload).FlattenAsync();
                var timeForMessages = timer.Elapsed - startTimeForBatch;
                var messageCount = messages.Count();

                Console.WriteLine($"Received message batch after {timer.Elapsed - startTimeForBatch} containing {messageCount} messages.");

                // Early out if no messages were returned
                if (messageCount == 0)
                    break;

                // Determine if we will require additional messages
                requestAdditionalMessages = messageCount == MessageRequestLimit;

                // Process the messages we received.
                foreach (IMessage msg in messages)
                {
                    messagesProcessed++;

                    if (cache.Messages.ContainsKey(msg.Id))
                        continue;

                    // Retrieve message information
                    ulong authorId = msg.Author.Id;
                    if (!cache.Authors.ContainsKey(authorId))
                        cache.Authors[authorId] = $"{msg.Author.Username}#{msg.Author.Discriminator}";

                    List<string> attachmentUrls = null;
                    if (msg.Attachments?.Count > 0)
                    {
                        attachmentUrls = new List<string>();
                        foreach (var att in msg.Attachments)
                        {
                            cache.AttachmentCache[att.Id] = att.Url;
                            attachmentUrls.Add($"!{att.Id}/{att.Filename}");
                        }
                    }


                        

                    IEnumerable<StrippedEmbed> embeds = null;
                    if (msg.Embeds?.Count > 0)
                        embeds = msg.Embeds.Select(emb => (StrippedEmbed)(Embed)emb); // Why? I don't fucking know!

                    // Update newest snowflake
                    ulong messageId = msg.Id;
                    if (messageId > cache.NewestSnowflake)
                        cache.NewestSnowflake = messageId;

                    // Add the message to the cache
                    cache.Messages.Add(messageId, new ChannelCacheMessage(msg.Content, authorId, attachmentUrls, embeds, msg.IsPinned));
                }

                Console.WriteLine($"Messages processed after {timer.Elapsed - startTimeForBatch}");

                // Save the cache to a file
                SaveChannelCache(cache);

                Console.WriteLine($"Cached saved at {DateTime.UtcNow}. Took {timer.Elapsed - startTimeForBatch} to fetch and process.");
            }

            timer.Stop();

            Console.WriteLine($"Done! Completed retrieval and cache of {messagesProcessed} messages in {timer.Elapsed}.");

            await UpdateContentCacheForChannel(cache);
        }

        public Task UpdateContentCacheForChannel(ChannelCache cache)
        {
            // If downloading attachments has been disabled, we'll just skip all of this.
            if (Program.Configuration["download-attachments"] != "true")
                return Task.CompletedTask;

            // We'll compare the attachments which should be cached according to the channel cache and download any which are missing.
            var semaphore = new SemaphoreSlim(MaxConcurrentDownloads, MaxConcurrentDownloads);
            var totalDownloads = 0;
            var downloadsCompleted = 0;
            foreach (var att in cache.AttachmentCache)
            {
                if (ContentCacheDir.GetFiles($"{att.Key}.*").Length > 0)
                    continue;

                totalDownloads++;

                Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        // Download the file to the cache directory
                        using FileStream file = File.Create(Path.Combine(ContentCacheDir.FullName, $"{att.Key}{Path.GetExtension(att.Value)}"));
                        Console.WriteLine($"{file.Name}\tDownloading");
                        using HttpResponseMessage response = await httpClient.GetAsync(att.Value);
                        using Stream downloadStream = await response.Content.ReadAsStreamAsync();
                        downloadStream.Seek(0, SeekOrigin.Begin);
                        downloadStream.CopyTo(file);
                        Console.WriteLine($"{file.Name}\tDone");
                    }
                    finally
                    {
                        semaphore.Release();
                        downloadsCompleted++;
                    }
                });
            }

            while (downloadsCompleted < totalDownloads)
                Task.Delay(SemaphoreCheckTime);

            semaphore.Dispose();

            return Task.CompletedTask;
        }

        /*
        When exported, to save on space, data is organised as Channel -> Author -> Message
        */

        internal struct ExportData
        {
            [JsonProperty("n", Order = 9)]
            public Dictionary<ulong, string> AuthorNames;

            [JsonProperty("channelCount", Order = 0)]
            public int? ChannelCount;

            [JsonProperty("messageCount", Order = 1)]
            public int? MessageCount;

            [JsonProperty("authorCount", Order = 2)]
            public int? AuthorCount;

            [JsonProperty("uniqueAttachmentCount", Order = 3)]
            public int? AttachmentCount;

            [JsonProperty("d", Order = 10)]
            public Dictionary<ulong, Dictionary<ulong, Dictionary<ulong, ExportDataMessage>>> Data; //Channel:Author:Message -> Content
        }

        internal struct ExportDataMessage
        {
            [JsonProperty("c", Order = 0)]
            public string Content;

            [JsonProperty("a", Order = 1)]
            public IEnumerable<string> AttachmentUrls;

            [JsonProperty("e", Order = 2)]
            public IEnumerable<StrippedEmbed> Embeds;

            [JsonProperty("p", Order = 3)]
            public bool? Pinned;

            public ExportDataMessage(string content, IEnumerable<string> attachments = null, IEnumerable<StrippedEmbed> embeds = null, bool pinned = false)
            {
                Content = content;
                AttachmentUrls = attachments;
                Embeds = embeds;
                Pinned = pinned;
            }

            public ExportDataMessage(ChannelCacheMessage msg)
            {
                Content = msg.Content;
                AttachmentUrls = msg.AttachmentUrls;
                Embeds = msg.Embeds;
                Pinned = msg.Pinned;
            }
        }

        private string GetExportPath(Guid exportId)
            => Path.Combine(ExportsDir.FullName, $"{exportId}/");

        public async Task ExportChannelsToArchive(SocketCommandContext context, ulong[] channelIds = null, bool includeStatistics = false)
        {
            // First, we need a GUID to identify this export
            var exportId = Guid.NewGuid();

            // We also need a directory for the export
            var thisExportDir = new DirectoryInfo(GetExportPath(exportId));
            thisExportDir.Create();

            // Now we need to get the cache's of the channels that were requested
            var caches = new List<ChannelCache>();

            // If no channels were specified, that means all cached channels within the guild or the DM channel.
            // Otherwise, get and cache all specified channels.
            if (!context.IsPrivate)
            {
                if (channelIds is null)
                {
                    foreach (var channel in context.Guild.Channels)
                    {
                        if (channel is ISocketMessageChannel)
                        {
                            if (File.Exists(GetChannelCachePath(channel.Id)))
                            {
                                await UpdateChannelCache(channel as ISocketMessageChannel);
                                caches.Add(GetChannelCache(channel.Id));
                            }
                        }
                    }

                    if (caches.Count == 0)
                    {
                        await UpdateChannelCache(context.Channel);
                        caches.Add(GetChannelCache(context.Channel.Id));
                    }
                }
                else
                {
                    foreach (ulong channelId in channelIds)
                    {
                        var channel = context.Guild.GetTextChannel(channelId);
                        if (channel != null)
                        {
                            await UpdateChannelCache(channel);
                            caches.Add(GetChannelCache(channelId));
                        }
                    }
                }
            }
            else
            {
                await UpdateChannelCache(context.Channel);
                caches.Add(GetChannelCache(context.Channel.Id));
            }

            // Now that we have all current caches for this channel, it's time to start building the dataset.
            var dataSet = new Dictionary<ulong, Dictionary<ulong, Dictionary<ulong, ExportDataMessage>>>();
            var collatedNames = new Dictionary<ulong, string>();
            var collatedAttachments = new HashSet<ulong>();
            int messageCount = 0;
            int authorCount = 0;

            // For each channel we:
            foreach (var cache in caches)
            {
                // Create a channel storage
                var channelStorage = new Dictionary<ulong, Dictionary<ulong, ExportDataMessage>>();
                dataSet[cache.Channel] = channelStorage;

                // Add the current name of the channel to the names dict (if we can find it!)
                collatedNames[cache.Channel] = "deleted-channel";
                if (discordClient.GetChannel(cache.Channel) is ISocketMessageChannel curChannel)
                    collatedNames[cache.Channel] = curChannel.Name;

                // Add all of the message author dictionaries and collate author names
                foreach (var author in cache.Authors)
                {
                    channelStorage[author.Key] = new Dictionary<ulong, ExportDataMessage>();
                    if (collatedNames.TryAdd(author.Key, author.Value))
                        authorCount++;
                }

                // Collate all of the attachments
                foreach (var att in cache.AttachmentCache)
                    collatedAttachments.Add(att.Key);

                // For each message in the channel, assign it's exported form to the author of the message
                foreach (var msg in cache.Messages)
                {
                    messageCount++;
                    channelStorage[msg.Value.Author][msg.Key] = new ExportDataMessage(msg.Value);
                }
            }

            // Now we create our large export data
            ExportData exportData = new ExportData
            {
                AuthorNames = collatedNames,
                Data = dataSet
            };

            await context.Channel.SendMessageAsync($"Message Count: {messageCount}\nAuthor Count: {authorCount}\nAttachment Count: {collatedAttachments.Count()}");

            // Add statistics if asked
            if (includeStatistics)
            {
                exportData.AttachmentCount = collatedAttachments.Count();
                exportData.AuthorCount = authorCount;
                exportData.ChannelCount = caches.Count;
                exportData.MessageCount = messageCount;
            }

            long estimatedSize = 0;

            // Write the export out to a file
            using (var exportDataFile = File.CreateText(Path.Combine(thisExportDir.FullName, "data.json")))
            {
                Serializer.Serialize(exportDataFile, exportData);
                estimatedSize += exportDataFile.BaseStream.Length;
            }

            // Copy the relevant attachments to the export directory (if requested)
            if (Program.Configuration["copy-attachments"] == "true")
            {
                foreach (var attachmentFile in ContentCacheDir.GetFiles("*.*"))
                {
                    if (ulong.TryParse(Path.GetFileNameWithoutExtension(attachmentFile.Name), out ulong attachmentId))
                    {
                        if (collatedAttachments.Contains(attachmentId))
                        {
                            attachmentFile.CopyTo(Path.Combine(thisExportDir.FullName, attachmentFile.Name));
                            estimatedSize += attachmentFile.Length;
                        }
                    }
                }
            }

            string finalMessage = $"Export {exportId} completed!";

            // And then finally, if it's small enough to be sent over discord, zip it up send it to the user who requested it via DM.
            if (Program.Configuration["send-exports"] == "true" && estimatedSize < DiscordMaxFileSize)
            {
                finalMessage += $"\nThe export content will be sent to you via DM...";

                // Now we zip up the export contents
                var zipFilePath = Path.Combine(ExportsDir.FullName, $"{exportId}.zip");
                ZipFile.CreateFromDirectory(thisExportDir.FullName, zipFilePath);

                // And then send it to the user who requested it
                var zipFile = new FileInfo(zipFilePath);

                if (zipFile.Exists)
                {
                    await context.User.SendFileAsync(zipFilePath);

                    // We don't need this export's directory anymore...
                    thisExportDir.Delete(true);
                }
                else
                {
                    finalMessage += "\nSorry, zipping failed. You'll need to manually download the export.";
                }
            }

            await context.Channel.SendMessageAsync(finalMessage);
        }
    }
}
