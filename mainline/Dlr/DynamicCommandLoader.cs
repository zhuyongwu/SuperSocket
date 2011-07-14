﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SuperSocket.SocketBase;
using SuperSocket.SocketBase.Command;
using System.Threading;
using SuperSocket.Common;
using Microsoft.Scripting.Hosting;

namespace SuperSocket.Dlr
{
    public class DynamicCommandLoader : ICommandLoader
    {
        private static ScriptRuntime m_ScriptRuntime;

        private static HashSet<string> m_CommandExtensions;

        private static Timer m_CommandDirCheckingTimer;

        private static readonly int m_CommandDirCheckingInterval = 1000 * 60 * 5;// 5 minutes

        static DynamicCommandLoader()
        {
            m_ScriptRuntime = ScriptRuntime.CreateFromConfiguration();

            List<string> fileExtensions = new List<string>();

            foreach (var fxts in m_ScriptRuntime.Setup.LanguageSetups.Select(s => s.FileExtensions))
                fileExtensions.AddRange(fxts);

            m_CommandExtensions = new HashSet<string>(fileExtensions, StringComparer.OrdinalIgnoreCase);

            m_ServerCommandStateLib = new Dictionary<string, ServerCommandState>(StringComparer.OrdinalIgnoreCase);
            m_CommandDirCheckingTimer = new Timer(OnCommandDirCheckingTimerCallback, null, m_CommandDirCheckingInterval, m_CommandDirCheckingInterval);
        }

        static IEnumerable<string> GetCommandFiles(string path, SearchOption option)
        {
            return Directory.GetFiles(path, "*.*", option).Where(f => m_CommandExtensions.Contains(Path.GetExtension(f)));
        }

        private static void OnCommandDirCheckingTimerCallback(object state)
        {
            m_CommandDirCheckingTimer.Change(Timeout.Infinite, Timeout.Infinite);

            try
            {
                var commandDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Command");
                var commonCommands = GetCommandFiles(commandDir, SearchOption.TopDirectoryOnly);

                foreach (var name in m_ServerCommandStateLib.Keys)
                {
                    var serverState = m_ServerCommandStateLib[name];

                    var commandSourceDict = serverState.Commands.ToDictionary(c => c.FilePath,
                        c => new CommandFileEntity { Command = c },
                        StringComparer.OrdinalIgnoreCase);

                    var serverCommands = commonCommands.ToList();
                    
                    var serverCommandDir = Path.Combine(commandDir, name);

                    if (Directory.Exists(serverCommandDir))
                    {
                        serverCommands.AddRange(GetCommandFiles(serverCommandDir, SearchOption.TopDirectoryOnly));
                    }

                    List<CommandUpdateInfo<CommandFileInfo>> updatedCommands = new List<CommandUpdateInfo<CommandFileInfo>>();

                    foreach (var c in serverCommands)
                    {
                        var lastUpdatedTime = File.GetLastWriteTime(c);

                        CommandFileEntity commandEntity;

                        if (commandSourceDict.TryGetValue(c, out commandEntity))
                        {
                            commandEntity.Processed = true;

                            if (commandEntity.Command.LastUpdatedTime != lastUpdatedTime)
                            {
                                //update command's last updated time in dictionary
                                commandEntity.Command.LastUpdatedTime = lastUpdatedTime;

                                updatedCommands.Add(new CommandUpdateInfo<CommandFileInfo>
                                    {
                                        UpdateAction = CommandUpdateAction.Update,
                                        Command = new CommandFileInfo
                                            {
                                                FilePath = c,
                                                LastUpdatedTime = lastUpdatedTime
                                            }
                                    });
                            }
                        }
                        else
                        {
                            commandSourceDict.Add(c, new CommandFileEntity
                                {
                                    Command = new CommandFileInfo
                                        {
                                            FilePath = c,
                                            LastUpdatedTime = lastUpdatedTime
                                        },
                                    Processed = true
                                });

                            updatedCommands.Add(new CommandUpdateInfo<CommandFileInfo>
                                {
                                    UpdateAction = CommandUpdateAction.Add,
                                    Command = new CommandFileInfo
                                    {
                                        FilePath = c,
                                        LastUpdatedTime = lastUpdatedTime
                                    }
                                });
                        }
                    }

                    foreach (var cmd in commandSourceDict.Values.Where(e => !e.Processed))
                    {
                        updatedCommands.Add(new CommandUpdateInfo<CommandFileInfo>
                            {
                                UpdateAction = CommandUpdateAction.Remove,
                                Command = new CommandFileInfo
                                {
                                    FilePath = cmd.Command.FilePath,
                                    LastUpdatedTime = cmd.Command.LastUpdatedTime
                                }
                            });
                    }

                    if (updatedCommands.Count > 0)
                    {
                        serverState.Commands = commandSourceDict.Values.Where(e => e.Processed).Select(e => e.Command).ToList();
                        serverState.CommandUpdater(updatedCommands);
                    }
                }
            }
            catch (Exception e)
            {
                LogUtil.LogError(e);
            }
            finally
            {
                m_CommandDirCheckingTimer.Change(m_CommandDirCheckingInterval, m_CommandDirCheckingInterval);
            }
        }

        class CommandFileEntity
        {
            public CommandFileInfo Command { get; set; }
            public bool Processed { get; set; }
        }

        class CommandFileInfo
        {
            public string FilePath { get; set; }
            public DateTime LastUpdatedTime { get; set; }
        }

        class ServerCommandState
        {
            public List<CommandFileInfo> Commands { get; set; }
            public Action<IEnumerable<CommandUpdateInfo<CommandFileInfo>>> CommandUpdater { get; set; }
        }

        private static Dictionary<string, ServerCommandState> m_ServerCommandStateLib;

        public bool LoadCommands<TAppSession, TCommandInfo>(IAppServer appServer, Func<ICommand<TAppSession, TCommandInfo>, bool> commandRegister, Action<IEnumerable<CommandUpdateInfo<ICommand<TAppSession, TCommandInfo>>>> commandUpdater)
            where TAppSession : IAppSession, IAppSession<TAppSession, TCommandInfo>, new()
            where TCommandInfo : ICommandInfo
        {
            if (m_ServerCommandStateLib.ContainsKey(appServer.Name))
                throw new Exception("This server's commands have been loaded already!");

            ServerCommandState serverCommandState = new ServerCommandState
            {
                CommandUpdater = (o) =>
                {
                    commandUpdater(UpdateCommands<TAppSession, TCommandInfo>(appServer, o));
                },
                Commands = new List<CommandFileInfo>()
            };


            var commandDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Command");
            var serverCommandDir = Path.Combine(commandDir, appServer.Name);

            if (!Directory.Exists(commandDir))
                return true;

            List<string> commandFiles = new List<string>();

            commandFiles.AddRange(GetCommandFiles(commandDir, SearchOption.TopDirectoryOnly));

            if (Directory.Exists(serverCommandDir))
            {
                commandFiles.AddRange(GetCommandFiles(serverCommandDir, SearchOption.TopDirectoryOnly));
            }

            if (!commandFiles.Any())
                return true;

            foreach (var file in commandFiles)
            {
                DynamicCommand<TAppSession, TCommandInfo> command;

                try
                {
                    var lastUpdatedTime = File.GetLastWriteTime(file);
                    command = new DynamicCommand<TAppSession, TCommandInfo>(m_ScriptRuntime, file, lastUpdatedTime);
                    serverCommandState.Commands.Add(new CommandFileInfo
                        {
                            FilePath = file,
                            LastUpdatedTime = lastUpdatedTime
                        });

                    if (!commandRegister(command))
                        return false;
                }
                catch (Exception e)
                {
                    throw new Exception("Failed to load command file: " + file + "!", e);
                }
            }

            m_ServerCommandStateLib.Add(appServer.Name, serverCommandState);

            return true;
        }

        private IEnumerable<CommandUpdateInfo<ICommand<TAppSession, TCommandInfo>>> UpdateCommands<TAppSession, TCommandInfo>(IAppServer appServer, IEnumerable<CommandUpdateInfo<CommandFileInfo>> updatedCommands)
            where TAppSession : IAppSession, IAppSession<TAppSession, TCommandInfo>, new()
            where TCommandInfo : ICommandInfo
        {
            return updatedCommands.Select(c =>
            {
                if (c.UpdateAction == CommandUpdateAction.Remove)
                {
                    return new CommandUpdateInfo<ICommand<TAppSession, TCommandInfo>>
                    {
                        Command = new MockupCommand<TAppSession, TCommandInfo>(Path.GetFileNameWithoutExtension(c.Command.FilePath)),
                        UpdateAction = c.UpdateAction
                    };
                }

                try
                {
                    var command = new DynamicCommand<TAppSession, TCommandInfo>(m_ScriptRuntime, c.Command.FilePath, c.Command.LastUpdatedTime);

                    return new CommandUpdateInfo<ICommand<TAppSession, TCommandInfo>>
                    {
                        Command = command,
                        UpdateAction = c.UpdateAction
                    };
                }
                catch (Exception e)
                {
                    appServer.Logger.LogError("Failed to load command file: " + c.Command.FilePath + "!", e);
                    return null;
                }
            });
        }
    }
}