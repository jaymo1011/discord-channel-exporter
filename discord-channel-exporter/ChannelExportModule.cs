using Discord;
using Discord.Commands;
using System.Threading.Tasks;

namespace DiscordBot
{
    [RequireOwner]
    [RequireBotPermission(ChannelPermission.ViewChannel)]
    [RequireBotPermission(ChannelPermission.ReadMessageHistory)]
    [RequireBotPermission(ChannelPermission.SendMessages)]
    public class ChannelExportModule : ModuleBase<SocketCommandContext>
    {
        public ChannelBackupService ChannelExport { get; set; }

        [Command("update")]
        public async Task UpdateChannelCache()
        {
            await ReplyAsync($"Updating the channel cache.");
            await ChannelExport.UpdateChannelCache(Context.Channel);
            await ReplyAsync($"Done!");
        }

        [Command("export")]
        public Task ExportChannel(params ulong[] channels)
            => ExportChannel(false, channels);

        [Command("export")]
        public async Task ExportChannel(bool includeStatistics, params ulong[] channels)
        {
            await ReplyAsync($"Attempting to export...");
            await ChannelExport.ExportChannelsToArchive(Context, channels, includeStatistics);
            await ReplyAsync($"Done!");
        }
    }
}
