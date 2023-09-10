using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.CommandAll;
using DSharpPlus.CommandAll.Parsers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OoLunar.DocBot.AssemblyProviders;
using OoLunar.DocBot.Events;
using OoLunar.DocBot.GitHub;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace OoLunar.DocBot
{
    public sealed class Program
    {
        private static readonly string[] _prefixes = new string[1] { "d" };

        public static async Task Main(string[] args)
        {
            IServiceCollection services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(services => new ConfigurationBuilder()
                .AddJsonFile("config.json", true, true)
#if DEBUG
                .AddJsonFile("config.debug.json", true, true)
#endif
                .AddEnvironmentVariables("DocBot_")
                .Build());

            services.AddLogging(loggerBuilder =>
            {
                const string loggingFormat = "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Level:u4}] {SourceContext}: {Message:lj}{NewLine}{Exception}";
                IConfiguration configuration = services.BuildServiceProvider().GetRequiredService<IConfiguration>();
                LoggerConfiguration loggingConfiguration = new LoggerConfiguration()
                    .MinimumLevel.Is(configuration.GetValue("logging:level", LogEventLevel.Debug))
                    .WriteTo.Console(outputTemplate: loggingFormat, formatProvider: CultureInfo.InvariantCulture, theme: new AnsiConsoleTheme(new Dictionary<ConsoleThemeStyle, string>
                    {
                        [ConsoleThemeStyle.Text] = "\x1b[0m",
                        [ConsoleThemeStyle.SecondaryText] = "\x1b[90m",
                        [ConsoleThemeStyle.TertiaryText] = "\x1b[90m",
                        [ConsoleThemeStyle.Invalid] = "\x1b[31m",
                        [ConsoleThemeStyle.Null] = "\x1b[95m",
                        [ConsoleThemeStyle.Name] = "\x1b[93m",
                        [ConsoleThemeStyle.String] = "\x1b[96m",
                        [ConsoleThemeStyle.Number] = "\x1b[95m",
                        [ConsoleThemeStyle.Boolean] = "\x1b[95m",
                        [ConsoleThemeStyle.Scalar] = "\x1b[95m",
                        [ConsoleThemeStyle.LevelVerbose] = "\x1b[34m",
                        [ConsoleThemeStyle.LevelDebug] = "\x1b[90m",
                        [ConsoleThemeStyle.LevelInformation] = "\x1b[36m",
                        [ConsoleThemeStyle.LevelWarning] = "\x1b[33m",
                        [ConsoleThemeStyle.LevelError] = "\x1b[31m",
                        [ConsoleThemeStyle.LevelFatal] = "\x1b[97;91m"
                    }))
                    .WriteTo.File(
                        $"logs/{DateTime.Now.ToUniversalTime().ToString("yyyy'-'MM'-'dd' 'HH'.'mm'.'ss", CultureInfo.InvariantCulture)}.log",
                        rollingInterval: RollingInterval.Day,
                        outputTemplate: loggingFormat,
                        formatProvider: CultureInfo.InvariantCulture
                    );

                // Allow specific namespace log level overrides, which allows us to hush output from things like the database basic SELECT queries on the Information level.
                foreach (IConfigurationSection logOverride in configuration.GetSection("logging:overrides").GetChildren())
                {
                    if (logOverride.Value is null || !Enum.TryParse(logOverride.Value, out LogEventLevel logEventLevel))
                    {
                        continue;
                    }

                    loggingConfiguration.MinimumLevel.Override(logOverride.Key, logEventLevel);
                }

                loggerBuilder.AddSerilog(loggingConfiguration.CreateLogger());
            });

            Assembly currentAssembly = typeof(Program).Assembly;
            services.AddSingleton((serviceProvider) =>
            {
                DiscordEventManager eventManager = new(serviceProvider);
                eventManager.GatherEventHandlers(currentAssembly);
                return eventManager;
            });

            services.AddSingleton((serviceProvider) =>
            {
                ILogger<GitHubRateLimitMessageHandler> logger = serviceProvider.GetRequiredService<ILogger<GitHubRateLimitMessageHandler>>();
                AssemblyInformationalVersionAttribute? assemblyInformationalVersion = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                HttpClient httpClient = new(new GitHubRateLimitMessageHandler(new HttpClientHandler(), logger));
#if DEBUG
                httpClient.DefaultRequestHeaders.Add("User-Agent", $"OoLunar.DocBot/{assemblyInformationalVersion?.InformationalVersion ?? "0.1.0"}-dev");
#else
                httpClient.DefaultRequestHeaders.Add("User-Agent", $"OoLunar.DocBot/{assemblyInformationalVersion?.InformationalVersion ?? "0.1.0"}");
#endif
                return httpClient;
            });

            services.AddSingleton<GitHubMetadataRetriever>();
            services.AddSingleton((serviceProvider) => new NugetAssemblyProvider(serviceProvider.GetRequiredService<IConfiguration>(), serviceProvider.GetRequiredService<ILogger<NugetAssemblyProvider>>()));
            services.AddSingleton((serviceProvider) =>
            {
                NugetAssemblyProvider assemblyProvider = serviceProvider.GetRequiredService<NugetAssemblyProvider>();
                GitHubMetadataRetriever github = serviceProvider.GetRequiredService<GitHubMetadataRetriever>();
                ILogger<DocumentationProvider> logger = serviceProvider.GetRequiredService<ILogger<DocumentationProvider>>();
                return new DocumentationProvider(assemblyProvider.GetAssembliesAsync, github, logger);
            });

            services.AddSingleton(serviceProvider =>
            {
                IConfiguration configuration = serviceProvider.GetRequiredService<IConfiguration>();
                DiscordEventManager eventManager = serviceProvider.GetRequiredService<DiscordEventManager>();
                DiscordShardedClient shardedClient = new(new DiscordConfiguration()
                {
                    Token = configuration.GetValue<string>("discord:token")!,
                    Intents = eventManager.Intents,
                    LoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>()
                });

                eventManager.RegisterEventHandlers(shardedClient);
                IReadOnlyDictionary<int, CommandAllExtension> commandAllShards = shardedClient.UseCommandAllAsync(new CommandAllConfiguration()
                {
#if DEBUG
                    DebugGuildId = configuration.GetValue<ulong?>("discord:debug_guild_id"),
#endif
                    PrefixParser = new PrefixParser(configuration.GetSection("discord:prefixes").Get<string[]>() ?? _prefixes),
                    ServiceProvider = serviceProvider
                }).GetAwaiter().GetResult();

                foreach (CommandAllExtension commandAll in commandAllShards.Values)
                {
                    commandAll.CommandManager.AddCommands(commandAll, currentAssembly);
                    commandAll.ArgumentConverterManager.AddArgumentConverters(currentAssembly);
                    eventManager.RegisterEventHandlers(commandAll);
                }

                return shardedClient;
            });

            IServiceProvider serviceProvider = services.BuildServiceProvider();
            ILogger<Program> logger = serviceProvider.GetRequiredService<ILogger<Program>>();
            IConfiguration configuration = serviceProvider.GetRequiredService<IConfiguration>();
            DocumentationProvider documentationProvider = serviceProvider.GetRequiredService<DocumentationProvider>();
            await documentationProvider.ReloadAsync();

            DiscordShardedClient discordShardedClient = serviceProvider.GetRequiredService<DiscordShardedClient>();
            await discordShardedClient.StartAsync();
            await Task.Delay(-1);
        }
    }
}
