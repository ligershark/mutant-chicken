﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.DotNet.Cli.Utils;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.TemplateEngine.Abstractions;

namespace dotnet_new3
{
    public class Program
    {
        internal static IBroker Broker { get; private set; }

        public static int Main(string[] args)
        {
            //Console.ReadLine();
            Broker = new Broker();

            CommandLineApplication app = new CommandLineApplication(false)
            {
                Name = "dotnet new3",
                FullName = "Template Instantiation Commands for .NET Core CLI."
            };

            CommandArgument template = app.Argument("template", "The template to instantiate.");
            CommandOption listOnly = app.Option("-l|--list", "Lists templates with containing the specified name.", CommandOptionType.NoValue);
            CommandOption name = app.Option("-n|--name", "The name for the output. If no name is specified, the name of the current directory is used.", CommandOptionType.SingleValue);
            CommandOption dir = app.Option("-d|--dir", "Indicates whether to display create a directory for the generated content.", CommandOptionType.NoValue);
            CommandOption alias = app.Option("-a|--alias", "Creates an alias for the specified template", CommandOptionType.SingleValue);
            CommandOption parametersFiles = app.Option("-x|--extra-args", "Adds a parameters file.", CommandOptionType.MultipleValue);
            CommandOption install = app.Option("-i|--install", "Installs a source or a template pack.", CommandOptionType.MultipleValue);
            CommandOption uninstall = app.Option("-u|--uninstall", "Uninstalls a source", CommandOptionType.MultipleValue);
            CommandOption source = app.Option("-s|--source", "The specific template source to get the template from.", CommandOptionType.SingleValue);
            CommandOption currentConfig = app.Option("-c|--current-config", "Lists the currently installed components and sources.", CommandOptionType.NoValue);
            CommandOption help = app.Option("-h|--help", "Indicates whether to display the help for the template's parameters instead of creating it.", CommandOptionType.NoValue);

            CommandOption installComponent = app.Option("--install-component", "Installs a component.", CommandOptionType.MultipleValue);
            CommandOption resetConfig = app.Option("--reset", "Resets the component cache and installed template sources.", CommandOptionType.NoValue);
            CommandOption rescan = app.Option("--rescan", "Rebuilds the component cache.", CommandOptionType.NoValue);
            CommandOption global = app.Option("--global", "Performs the --install or --install-component operation for all users.", CommandOptionType.NoValue);
            CommandOption reinit = app.Option("--reinitialize", "Sets dotnet new3 back to its pre-first run state.", CommandOptionType.NoValue);
            CommandOption quiet = app.Option("--quiet", "Doesn't output any status information.", CommandOptionType.NoValue);
            CommandOption skipUpdateCheck = app.Option("--skip-update-check", "Don't check for updates.", CommandOptionType.NoValue);
            CommandOption update = app.Option("--update", "Update matching templates.", CommandOptionType.NoValue);

            app.OnExecute(() =>
            {
                if (reinit.HasValue())
                {
                    Paths.FirstRunCookie.Delete();
                    return Task.FromResult(0);
                }

                if (!Paths.UserDir.Exists() || !Paths.FirstRunCookie.Exists())
                {
                    if (!quiet.HasValue())
                    {
                        Reporter.Output.WriteLine("Getting things ready for first use...");
                    }

                    if (!Paths.FirstRunCookie.Exists())
                    {
                        PerformReset(true, true);
                        Paths.FirstRunCookie.WriteAllText("");
                    }

                    ConfigureEnvironment();
                    PerformReset(false, true);
                }

                if (rescan.HasValue())
                {
                    Broker.ComponentRegistry.ForceReinitialize();
                    ShowConfig();
                    return Task.FromResult(0);
                }

                if (resetConfig.HasValue())
                {
                    PerformReset(global.HasValue());
                    return Task.FromResult(0);
                }

                if (currentConfig.HasValue())
                {
                    ShowConfig();
                    return Task.FromResult(0);
                }

                if (install.HasValue())
                {
                    InstallPackage(install.Values, true, global.HasValue(), quiet.HasValue());
                    return Task.FromResult(0);
                }

                if (installComponent.HasValue())
                {
                    InstallPackage(installComponent.Values, false, global.HasValue(), quiet.HasValue());
                    return Task.FromResult(0);
                }

                if (update.HasValue())
                {
                    return PerformUpdateAsync(template.Value, quiet.HasValue(), source);
                }

                if (uninstall.HasValue())
                {
                    foreach (string value in uninstall.Values)
                    {
                        if (value == "*")
                        {
                            Paths.TemplateSourcesFile.Delete();
                            Paths.AliasesFile.Delete();

                            if (global.HasValue())
                            {
                                Paths.GlobalTemplateCacheDir.Delete();
                            }
                            else
                            {
                                Paths.TemplateCacheDir.Delete();
                            }

                            return Task.FromResult(0);
                        }

                        if (!TryRemoveSource(value))
                        {
                            string cacheDir = global.HasValue() ? Paths.GlobalTemplateCacheDir : Paths.TemplateCacheDir;
                            bool anyRemoved = false;

                            if (!value.Exists())
                            {
                                foreach(string file in cacheDir.EnumerateFiles($"{value}.*.nupkg"))
                                {
                                    int verStart = file.IndexOf(value, StringComparison.OrdinalIgnoreCase) + value.Length + 1;
                                    string ver = file.Substring(verStart);
                                    ver = ver.Substring(0, ver.Length - ".nupkg".Length);
                                    Version version;

                                    if (Version.TryParse(ver, out version))
                                    {
                                        if (!quiet.HasValue())
                                        {
                                            Reporter.Output.WriteLine($"Removing {value} version {version}...");
                                        }

                                        anyRemoved = true;
                                        file.Delete();
                                    }
                                }
                            }

                            if (!anyRemoved && !quiet.HasValue())
                            {
                                Reporter.Error.WriteLine($"Couldn't remove {value} as a template source.".Red().Bold());
                            }
                        }
                    }

                    return Task.FromResult(0);
                }

                if (listOnly.HasValue())
                {
                    ListTemplates(template, source);
                    return Task.FromResult(0);
                }

                IReadOnlyDictionary<string, string> parameters;

                try
                {
                    parameters = app.ParseExtraArgs(parametersFiles);
                }
                catch (Exception ex)
                {
                    Reporter.Error.WriteLine(ex.Message.Red().Bold());
                    app.ShowHelp();
                    return Task.FromResult(-1);
                }

                return TemplateCreator.Instantiate(app, template.Value ?? "", name, dir, source, help, alias, parameters, quiet.HasValue(), skipUpdateCheck.HasValue());
            });

            int result;
            try
            {
                result = app.Execute(args);
            }
            catch (Exception ex)
            {
                AggregateException ax = ex as AggregateException;

                while (ax != null && ax.InnerExceptions.Count == 1)
                {
                    ex = ax.InnerException;
                    ax = ex as AggregateException;
                }

                Reporter.Error.WriteLine(ex.Message.Bold().Red());

                while (ex.InnerException != null)
                {
                    ex = ex.InnerException;
                    ax = ex as AggregateException;

                    while (ax != null && ax.InnerExceptions.Count == 1)
                    {
                        ex = ax.InnerException;
                        ax = ex as AggregateException;
                    }

                    Reporter.Error.WriteLine(ex.Message.Bold().Red());
                }

                Reporter.Error.WriteLine(ex.StackTrace.Bold().Red());
                result = 1;
            }

            return result;
        }

        private static async Task<int> PerformUpdateAsync(string name, bool quiet, CommandOption source)
        {
            HashSet<IConfiguredTemplateSource> allSources = new HashSet<IConfiguredTemplateSource>();
            HashSet<string> toInstall = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (ITemplate template in TemplateCreator.List(name, source))
            {
                allSources.Add(template.Source);
            }

            foreach (IConfiguredTemplateSource src in allSources)
            {
                if (!quiet)
                {
                    Reporter.Output.WriteLine($"Checking for updates for {src.Alias}...");
                }

                bool updatesReady;

                if (src.ParentSource != null)
                {
                    updatesReady = await src.Source.CheckForUpdatesAsync(src.ParentSource, src.Location);
                }
                else
                {
                    updatesReady = await src.Source.CheckForUpdatesAsync(src.Location);
                }

                if (updatesReady)
                {
                    if (!quiet)
                    {
                        Reporter.Output.WriteLine($"An update for {src.Alias} is available...");
                    }

                    string packageId = src.ParentSource != null
                        ? src.Source.GetInstallPackageId(src.ParentSource, src.Location)
                        : src.Source.GetInstallPackageId(src.Location);

                    toInstall.Add(packageId);
                }
            }

            if(toInstall.Count == 0)
            {
                if (!quiet)
                {
                    Reporter.Output.WriteLine("No updates were found.");
                }

                return 0;
            }

            if (!quiet)
            {
                Reporter.Output.WriteLine("Installing updates...");
            }

            List<string> installCommands = new List<string>();
            List<string> uninstallCommands = new List<string>();

            foreach (string packageId in toInstall)
            {
                installCommands.Add("-i");
                installCommands.Add(packageId);

                uninstallCommands.Add("-i");
                uninstallCommands.Add(packageId);
            }

            installCommands.Add("--quiet");
            uninstallCommands.Add("--quiet");

            Command.CreateDotNet("new3", uninstallCommands).ForwardStdOut().ForwardStdErr().Execute();
            Command.CreateDotNet("new3", installCommands).ForwardStdOut().ForwardStdErr().Execute();
            Broker.ComponentRegistry.ForceReinitialize();

            if (!quiet)
            {
                Reporter.Output.WriteLine("Done.");
            }

            return 0;
        }

        private static void ConfigureEnvironment()
        {
            string userNuGetConfig = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <packageSources>
    <add key=""dotnet new3 builtins"" value = ""{Paths.BuiltInsFeed}""/>
    <add key=""CLI Dependencies"" value=""https://dotnet.myget.org/F/cli-deps/api/v3/index.json"" />
  </packageSources>
</configuration>";

            Paths.UserNuGetConfig.WriteAllText(userNuGetConfig);
            string[] packages = Paths.DefaultInstallPackageList.ReadAllText().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (packages.Length > 0)
            {
                InstallPackage(packages, false, true, true);
            }

            packages = Paths.DefaultInstallTemplateList.ReadAllText().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (packages.Length > 0)
            {
                InstallPackage(packages, true, true, true);
            }
        }

        private static void InstallPackage(IReadOnlyList<string> packages, bool installingTemplates, bool global, bool quiet = false)
        {
            NuGetUtil.InstallPackage(packages, installingTemplates, global, quiet, TryAddSource);
            Broker.ComponentRegistry.ForceReinitialize();

            if (!quiet)
            {
                ListTemplates(new CommandArgument(), new CommandOption("--notReal", CommandOptionType.SingleValue));
            }
        }

        private static void PerformReset(bool global, bool quiet = false)
        {
            if (global)
            {
                Paths.GlobalComponentsDir.Delete();
                Paths.GlobalComponentCacheFile.Delete();
                Paths.GlobalTemplateCacheDir.Delete();
            }

            Paths.ComponentsDir.Delete();
            Paths.TemplateCacheDir.Delete();
            Paths.ScratchDir.Delete();
            Paths.TemplateSourcesFile.Delete();
            Paths.AliasesFile.Delete();
            Paths.ComponentCacheFile.Delete();

            Paths.TemplateCacheDir.CreateDirectory();
            Paths.ComponentsDir.CreateDirectory();
            Broker.ComponentRegistry.ForceReinitialize();
            TryAddSource(Paths.TemplateCacheDir);
            TryAddSource(Paths.GlobalTemplateCacheDir);

            if (!quiet)
            {
                ShowConfig();
            }
        }

        private static void ListTemplates(CommandArgument template, CommandOption source)
        {
            IEnumerable<ITemplate> results = TemplateCreator.List(template.Value, source);
            TableFormatter.Print(results, "(No Items)", "   ", '-', new Dictionary<string, Func<ITemplate, string>>
            {
                {"Templates", x => x.Name},
                {"Short Names", x => $"[{x.ShortName}]"},
                {"Alias", x => AliasRegistry.GetAliasForTemplate(x) ?? ""}
            });
        }

        private static bool TryRemoveSource(string value)
        {
            return Broker.RemoveConfiguredSource(value);
        }

        private static void ShowConfig()
        {
            Reporter.Output.WriteLine("dotnet new3 current configuration:");
            Reporter.Output.WriteLine(" ");
            TableFormatter.Print(Broker.GetConfiguredSources(), "(No Items)", "   ", '-', new Dictionary<string, Func<IConfiguredTemplateSource, string>>
            {
                { "Template Sources", x => x.Location }
            });

            TableFormatter.Print(Broker.ComponentRegistry.OfType<ITemplateSource>(), "(No Items)", "   ", '-', new Dictionary<string, Func<ITemplateSource, string>>
            {
                { "Template Source Readers", x => x.Name },
                { "Assembly", x => x.GetType().GetTypeInfo().Assembly.FullName }
            });

            TableFormatter.Print(Broker.ComponentRegistry.OfType<IGenerator>(), "(No Items)", "   ", '-', new Dictionary<string, Func<IGenerator, string>>
            {
                { "Generators", x => x.Name },
                { "Assembly", x => x.GetType().GetTypeInfo().Assembly.FullName }
            });
        }

        private static bool TryAddSource(string value)
        {
            ITemplateSource source = null;
            foreach (ITemplateSource src in Broker.ComponentRegistry.OfType<ITemplateSource>())
            {
                if (src.CanHandle(value))
                {
                    source = src;
                }
            }

            if (source == null)
            {
                return false;
            }

            Broker.AddConfiguredSource(value, source.Name, value);
            return true;
        }
    }

    internal class TableFormatter
    {
        public static void Print<T>(IEnumerable<T> items, string noItemsMessage, string columnPad, char header, Dictionary<string, Func<T, string>> dictionary)
        {
            List<string>[] columns = new List<string>[dictionary.Count];

            for (int i = 0; i < dictionary.Count; ++i)
            {
                columns[i] = new List<string>();
            }

            string[] headers = new string[dictionary.Count];
            int[] columnWidths = new int[dictionary.Count];
            int valueCount = 0;

            foreach (T item in items)
            {
                int index = 0;
                foreach (KeyValuePair<string, Func<T, string>> act in dictionary)
                {
                    headers[index] = act.Key;
                    columns[index++].Add(act.Value(item));
                }
                ++valueCount;
            }

            if (valueCount > 0)
            {
                for (int i = 0; i < columns.Length; ++i)
                {
                    columnWidths[i] = Math.Max(columns[i].Max(x => x.Length), headers[i].Length);
                }
            }
            else
            {
                int index = 0;
                foreach (KeyValuePair<string, Func<T, string>> act in dictionary)
                {
                    headers[index] = act.Key;
                    columnWidths[index++] = act.Key.Length;
                }
            }

            int headerWidth = columnWidths.Sum() + columnPad.Length * (dictionary.Count - 1);

            for (int i = 0; i < headers.Length - 1; ++i)
            {
                Reporter.Output.Write(headers[i].PadRight(columnWidths[i]));
                Reporter.Output.Write(columnPad);
            }

            Reporter.Output.WriteLine(headers[headers.Length - 1]);
            Reporter.Output.WriteLine("".PadRight(headerWidth, header));

            for (int i = 0; i < valueCount; ++i)
            {
                for (int j = 0; j < columns.Length - 1; ++j)
                {
                    Reporter.Output.Write(columns[j][i].PadRight(columnWidths[j]));
                    Reporter.Output.Write(columnPad);
                }

                Reporter.Output.WriteLine(columns[headers.Length - 1][i]);
            }

            if (valueCount == 0)
            {
                Reporter.Output.WriteLine(noItemsMessage);
            }

            Reporter.Output.WriteLine(" ");
        }
    }
}
