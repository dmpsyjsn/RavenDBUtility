﻿using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using ExportRavenDB2_5.Extensions;
using Raven.Client;
using Serilog;

namespace ExportRavenDB2_5.Common
{
    public class SmugglerWrapper2_5 : ISmugglerWrapper2_5
    {
        private readonly IDocumentStore _store;
        private readonly ILogger _logger;

        private readonly double _breakTimeSeconds;

        private string _backupDir;
        private readonly string _ravenDumpExtension;
        public string BackupDir
        {
            get
            {
                return _backupDir;
            }
            set
            {
                if (value == null)
                    throw new ArgumentNullException(nameof(value));

                if (value != string.Empty && !Directory.Exists(value))
                {
                    Directory.CreateDirectory(value);
                }

                _backupDir = value;
            }
        }

        public SmugglerWrapper2_5(IDocumentStore store, ILogger logger)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            _store = store;
            _logger = logger;

            _breakTimeSeconds = 5;
            BackupDir = string.Empty; //From current Dir

            _ravenDumpExtension = ".ravendump";
        }

        //We use Console Process and Smuggler.exe 3.5 for this
        public void ExportDatabaseNativeProcess(string databaseName, params string[] additionalSmugglerArguments)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                _logger.Warning("Database name incorrectly");
                return;
            }

            _logger.Information("Export database {0} with process", databaseName);

            BackupDir.EnsureFileDestination();
            var filePath = GetFilePathFromDatabaseName(databaseName);

            var actionPath = $"out {_store.Url} ";
            var smugglerOptionArguments = $" {string.Join(" ", additionalSmugglerArguments)}";

            var smugglerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Raven.Smuggler.2.5.exe");
            var smugglerArgs = string.Concat(actionPath, filePath, " --database=", databaseName, smugglerOptionArguments);

            try
            {
                //TODO probably need to add this event when exitCode != 0 or something and also consider other way
                var exitCode = StartSmugglerProcess(smugglerPath, smugglerArgs);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"An error occurred while trying to export {databaseName} with exception: {ex}");
            }

            Thread.Sleep(TimeSpan.FromSeconds(_breakTimeSeconds));
        }

        //We use Console Process and Smuggler.exe 3.5 for this
        public void ImportDatabaseNativeProcess(string databaseName, params string[] additionalSmugglerArguments)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                _logger.Warning("Database name incorrectly");
                return;
            }

            _logger.Information("Import database {0} with process", databaseName);

            BackupDir.EnsureFileDestination();
            var filePath = GetFilePathFromDatabaseName(databaseName);

            var actionPath = $"in {_store.Url} ";
            var smugglerOptionArguments = $" --negative-metadata-filter:@id=Raven/Encryption/Verification {string.Join(" ", additionalSmugglerArguments)}";

            var smugglerPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Raven.Smuggler.2.5.exe");
            var smugglerArgs = string.Concat(actionPath, filePath, " --database=", databaseName, smugglerOptionArguments);

            try
            {
                var exitCode = StartSmugglerProcess(smugglerPath, smugglerArgs);

                // if we have fail, we try do it again
                if (exitCode != 0)
                {
                    _logger.Warning("Smuggler failed the first time");
                    _logger.Warning($"Sleeping for {_breakTimeSeconds} seconds before trying again to backup {databaseName}");
                    Thread.Sleep(TimeSpan.FromSeconds(_breakTimeSeconds));

                    _logger.Information("Trying to export again");

                    var exitCodeTry = StartSmugglerProcess(smugglerPath, smugglerArgs);
                    if (exitCodeTry != 0)
                    {
                        //TODO probably need to add this event or something, consider optional Exception
                        throw new Exception($"Process {smugglerPath} didn't work with arguments {smugglerArgs}");
                    }

                    Thread.Sleep(TimeSpan.FromSeconds(_breakTimeSeconds));
                    _logger.Information("Succeeded the second time for {0}", databaseName);
                }
                else
                {
                    Thread.Sleep(TimeSpan.FromSeconds(_breakTimeSeconds));
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"An error occurred while trying to backup {databaseName} with exception: {ex}");
            }
        }

        private string GetFilePathFromDatabaseName(string databaseName)
        {
            if (!databaseName.EndsWith(_ravenDumpExtension))
                databaseName = $"{databaseName}{_ravenDumpExtension}";

            var filePath = Path.Combine(BackupDir, databaseName);

            return filePath;
        }

        private int StartSmugglerProcess(string smugglerPath, string smugglerArgs)
        {
            _logger.Information("Smuggler Path = {0}", smugglerPath);
            _logger.Information("Smuggler Args = {0}", smugglerArgs);

            using (var proc = new Process
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    FileName = smugglerPath,
                    Arguments = smugglerArgs
                }
            })
            {
                proc.Start();

                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                var code = proc.ExitCode;

                if (code != 0)
                {
                    _logger.Warning($"Process {smugglerPath} didn't work with arguments {smugglerArgs}");
                    _logger.Warning($"Smuggler process output = {output}");
                }
                else
                {
                    _logger.Information($"Smuggler process output = {output}");
                }

                return code;
            }
        }
    }
}
