﻿using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Raven.Abstractions.Data;
using Raven.Client;
using RestoreRavenDBs.Common;
using RestoreRavenDBs.Extensions;
using Serilog;

namespace RestoreRavenDBs.Handlers
{
    public class RestoreRavenDbHandler : IRestoreRavenDbHandler
    {
        private readonly IDocumentStore _store;
        private readonly ILogger _logger;
        private readonly ISmugglerWrapper _smugglerWrapper;

        private readonly string _backupDir;
        private readonly string _ravenDumpExtension;

        public RestoreRavenDbHandler(IDocumentStore store, ILogger logger, ISmugglerWrapper smugglerWrapper = null)
        {
            _store = store;
            _logger = logger;

            _smugglerWrapper = smugglerWrapper ?? new SmugglerWrapper(_store, _logger);

            _backupDir = ConfigurationManager.AppSettings["DefaultBackupDir"];
            _ravenDumpExtension = ".ravendump";
        }

        public RestoreRavenDbHandler(IDocumentStore store, ILogger logger, string backupDir, ISmugglerWrapper smugglerWrapper = null) 
            : this(store, logger, smugglerWrapper)
        {
            _backupDir = backupDir;
        }

        public void SmugglerFullExport(Func<string, bool> conditionForDatabaseName = null)
        {
            var sysCommands = _store.DatabaseCommands.ForSystemDatabase();

            var index = 0;
            var filteredDatabaseNames = new List<string>();

            var databaseNames = _store.DatabaseCommands.GlobalAdmin.GetDatabaseNames(100, index);

            while (databaseNames.Any())
            {
                filteredDatabaseNames.AddRange(from dbName in databaseNames
                                               where conditionForDatabaseName != null && conditionForDatabaseName(dbName)
                                               let doc = sysCommands.Get("Raven/Databases/" + dbName)
                                               let d = doc.DataAsJson
                                               let disabled = d.Value<bool>("Disabled")
                                               where !disabled
                                               select dbName);

                index += databaseNames.Length;

                databaseNames = _store.DatabaseCommands.GlobalAdmin.GetDatabaseNames(100, index);
            }

            _logger.Information("Total databases to backup = " + filteredDatabaseNames.Count);

            foreach (var dbName in filteredDatabaseNames)
            {
                _smugglerWrapper.ExportDatabaseNativeProcess(dbName);
            }
        }

        public void SmugglerFullExport(string databaseName)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                _logger.Warning("Database name incorrectly");
                return;
            }

            _smugglerWrapper.ExportDatabaseNativeProcess(databaseName);
        }

        public void SmugglerFullImport(Func<string, bool> conditionForDatabaseName = null)
        {
            var searchFilePattern = "*" + _ravenDumpExtension;

            var files = Directory.GetFiles(_backupDir, searchFilePattern, SearchOption.TopDirectoryOnly);

            if (conditionForDatabaseName != null)
                files = files.Where(conditionForDatabaseName).ToArray();

            var databaseNamesInOrder = files.Select(Path.GetFileNameWithoutExtension).OrderBy(x => x);

            foreach (var dbName in databaseNamesInOrder)
            {
                DeleteDatabase(dbName);
            }

            _logger.Information("Done Deleting {0} database", databaseNamesInOrder.Count());

            foreach (var dbName in databaseNamesInOrder)
            {
                _logger.Information("The database to restore = {0}", dbName);

                CreateDatabase(dbName);

                _smugglerWrapper.ImportDatabaseNativeProcess(dbName, "--disable-versioning-during-import");
            }
        }

        public void SmugglerFullImport(string databaseName)
        {
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                _logger.Warning("Database name incorrectly");
                return;
            }

            DeleteDatabase(databaseName);

            CreateDatabase(databaseName);

            _smugglerWrapper.ImportDatabaseNativeProcess(databaseName, "--disable-versioning-during-import");
        }

        public void CreateDatabase(string databaseName, params string[] additionalBundles)
        {
            if (_store.DatabaseExists(databaseName)) return;

            string[] defaultBundles = { "Encryption", "Compression" };
            var key = GenerateKey();

            _store.DatabaseCommands.GlobalAdmin.CreateDatabase(new DatabaseDocument
            {
                Id = databaseName,
                Settings =
                {
                    {"Raven/DataDir", "~\\" + databaseName},
                    {"Raven/ActiveBundles", string.Join(";", defaultBundles.Union(additionalBundles))},
                },
                SecuredSettings =
                {
                    {"Raven/Encryption/Key", key},
                    {
                        "Raven/Encryption/Algorithm", "System.Security.Cryptography.RijndaelManaged, mscorlib"
                    },
                    {"Raven/Encryption/KeyBitsPreference", "256"},
                    {"Raven/Encryption/EncryptIndexes", "True"}
                }
            });
        }

        public void DeleteDatabase(string databaseName)
        {
            if (!_store.DatabaseExists(databaseName)) return;

            _logger.Information("Deleting database = {0}", databaseName);

            _store.DatabaseCommands.GlobalAdmin.DeleteDatabase(databaseName, hardDelete: true);

            _logger.Information("Deletion complete");
        }

        private static string GenerateKey()
        {
            using (var crypt = Rijndael.Create())
            {
                crypt.GenerateKey();
                return Convert.ToBase64String(crypt.Key);
            }
        }
    }
}
