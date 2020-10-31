using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordBot
{
    class Program
    {
        public static IConfiguration Configuration;

        static void Main(string[] args)
        {
            Configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("config.json")
                .AddCommandLine(args)
                .Build();

            new Program().Main().GetAwaiter().GetResult();
        }

        public async Task Main()
        {
            using var services = ConfigureServices();

            // Get the client service
            var client = services.GetRequiredService<DiscordSocketClient>();

            // Set up the log function
            client.Log += msg => {
                Console.WriteLine(msg.ToString());
                return Task.CompletedTask;
            };

            // Login to the Discord API
            await client.LoginAsync(TokenType.Bot, Configuration["token"], true);

            // Start communicating as a bot
            await client.StartAsync();

            // Initialise the command handler
            await services.GetRequiredService<CommandHandlingService>().InitializeAsync();

            // Block this task until the program is closed
            await Task.Delay(Timeout.Infinite);
        }

        private ServiceProvider ConfigureServices()
            => new ServiceCollection()
                .AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
                {
                    ExclusiveBulkDelete = false
                }))
                .AddSingleton(new CommandService(new CommandServiceConfig
                {
                    CaseSensitiveCommands = false,
                    DefaultRunMode = RunMode.Async,
                    IgnoreExtraArgs = true
                }))
                .AddSingleton<CommandHandlingService>()
                .AddSingleton<HttpClient>()
                .AddSingleton<ChannelBackupService>()
                .BuildServiceProvider();
    }
}
