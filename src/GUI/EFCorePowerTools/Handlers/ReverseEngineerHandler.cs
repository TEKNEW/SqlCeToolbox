﻿using EnvDTE;
using ErikEJ.SqlCeToolbox.Dialogs;
using ErikEJ.SqlCeToolbox.Helpers;
using ReverseEngineer20;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Serialization.Json;
using System.Text;

namespace EFCorePowerTools.Handlers
{
    internal class ReverseEngineerHandler
    {
        private readonly EFCorePowerToolsPackage _package;

        public ReverseEngineerHandler(EFCorePowerToolsPackage package)
        {
            _package = package;
        }

        public async void ReverseEngineerCodeFirst(Project project)
        {
            try
            {
                var dteH = new EnvDteHelper();
                var revEng = new EfCoreReverseEngineer();

                if (_package.Dte2.Mode == vsIDEMode.vsIDEModeDebug)
                {
                    EnvDteHelper.ShowError("Cannot generate code while debugging");
                    return;
                }

                var startTime = DateTime.Now;
                var projectPath = project.Properties.Item("FullPath").Value.ToString();
                var optionsPath = Path.Combine(projectPath, "efpt.config.json");

                var databaseList = EnvDteHelper.GetDataConnections(_package);

                var psd = new PickServerDatabaseDialog(databaseList, _package);
                var diagRes = psd.ShowModal();
                if (!diagRes.HasValue || !diagRes.Value) return;

                // Show dialog with SqlClient selected by default
                _package.Dte2.StatusBar.Text = "Loading schema information...";

                var dbInfo = psd.SelectedDatabase.Value;

                if (dbInfo.DatabaseType == DatabaseType.SQLCE35)
                {
                    EnvDteHelper.ShowError($"Unsupported provider: {dbInfo.ServerVersion}");
                    return;
                }

                var options = TryReadOptions(optionsPath);

                var ptd = new PickTablesDialog { IncludeTables = true };
                using (var repository = RepositoryHelper.CreateRepository(dbInfo))
                {
                    ptd.Tables = repository.GetAllTableNamesForExclusion();
                }

                if (options != null && options.Tables.Count > 0)
                {
                    ptd.SelectedTables = options.Tables;
                }

                var res = ptd.ShowModal();
                if (!res.HasValue || !res.Value) return;

                var classBasis = RepositoryHelper.GetClassBasis(dbInfo.ConnectionString, dbInfo.DatabaseType);
                var model = revEng.GenerateClassName(classBasis) + "Context";
                var packageResult = dteH.ContainsEfCoreReference(project, dbInfo.DatabaseType);

                var modelDialog = new EfCoreModelDialog(options)
                {
                    InstallNuGetPackage = !packageResult.Item1,
                    ModelName = options != null ? options.ContextClassName : model,
                    ProjectName = project.Name,
                    NameSpace = options != null ? options.ProjectRootNamespace : project.Properties.Item("DefaultNamespace").Value.ToString()
                };

                _package.Dte2.StatusBar.Text = "Getting options...";
                var result = modelDialog.ShowModal();
                if (!result.HasValue || result.Value != true)
                    return;

                options = new ReverseEngineerOptions
                {
                    UseFluentApiOnly = !modelDialog.UseDataAnnotations,
                    ConnectionString = dbInfo.ConnectionString,
                    ContextClassName = modelDialog.ModelName,
                    DatabaseType = (ReverseEngineer20.DatabaseType)dbInfo.DatabaseType,
                    ProjectPath = projectPath,
                    OutputPath = modelDialog.OutputPath,
                    ProjectRootNamespace = modelDialog.NameSpace,
                    UseDatabaseNames = modelDialog.UseDatabaseNames,
                    UseInflector = modelDialog.UsePluralizer,
                    IdReplace = modelDialog.ReplaceId,
                    UseHandleBars = modelDialog.UseHandelbars,
                    IncludeConnectionString = modelDialog.IncludeConnectionString,
                    SelectedToBeGenerated = modelDialog.SelectedTobeGenerated,
                    Tables = ptd.Tables
                };

                _package.Dte2.StatusBar.Text = "Generating code...";

                bool isNetCore = project.Properties.Item("TargetFrameworkMoniker").Value.ToString().Contains(".NETCoreApp,Version=v2.0");

                if (modelDialog.UseHandelbars)
                {
                    var dropped = (DropTemplates(projectPath));
                    if (dropped && !isNetCore)
                    {
                        project.ProjectItems.AddFromDirectory(Path.Combine(projectPath, "CodeTemplates"));
                    }
                }

                var revEngResult = revEng.GenerateFiles(options);

                if (modelDialog.SelectedTobeGenerated == 0 || modelDialog.SelectedTobeGenerated == 2)
                {
                    foreach (var filePath in revEngResult.EntityTypeFilePaths)
                    {
                        project.ProjectItems.AddFromFile(filePath);
                    }
                }
                if (modelDialog.SelectedTobeGenerated == 0 || modelDialog.SelectedTobeGenerated == 1)
                {
                    project.ProjectItems.AddFromFile(revEngResult.ContextFilePath);
                    if (!isNetCore)
                    {
                        _package.Dte2.ItemOperations.OpenFile(revEngResult.ContextFilePath);
                    }
                }

                var missingProviderPackage = packageResult.Item1 ? null : packageResult.Item2;
                if (modelDialog.InstallNuGetPackage)
                {
                    missingProviderPackage = null;
                }

                _package.Dte2.StatusBar.Text = "Reporting result...";
                var errors = ReportRevEngErrors(revEngResult, missingProviderPackage);

                SaveOptions(project, optionsPath, options);

                if (modelDialog.InstallNuGetPackage)
                {
                    _package.Dte2.StatusBar.Text = "Installing EF Core provider package";
                    var nuGetHelper = new NuGetHelper();
                    await nuGetHelper.InstallPackageAsync(packageResult.Item2, project);
                }
                var duration = DateTime.Now - startTime;
                _package.Dte2.StatusBar.Text = $"Reverse engineer completed in {duration:h\\:mm\\:ss}";

                EnvDteHelper.ShowMessage(errors);

                if (revEngResult.EntityErrors.Count > 0)
                {
                    _package.LogError(revEngResult.EntityErrors, null);
                }
                if (revEngResult.EntityWarnings.Count > 0)
                {
                    _package.LogError(revEngResult.EntityWarnings, null);
                }
                Telemetry.TrackEvent("PowerTools.ReverseEngineer");
            }
            catch (AggregateException ae)
            {
                foreach (var innerException in ae.Flatten().InnerExceptions)
                {
                    _package.LogError(new List<string>(), innerException);
                }
            }
            catch (Exception exception)
            {
                _package.LogError(new List<string>(), exception);
            }
        }

        private void SaveOptions(Project project, string optionsPath, ReverseEngineerOptions options)
        {
            if (!File.Exists(optionsPath + ".ignore"))
            {
                File.WriteAllText(optionsPath, WriteOptions(options), Encoding.UTF8);
                project.ProjectItems.AddFromFile(optionsPath);
            }
        }

        private string ReportRevEngErrors(EfCoreReverseEngineerResult revEngResult, string missingProviderPackage)
        {
            var errors = new StringBuilder();
            if (revEngResult.EntityErrors.Count == 0)
            {
                errors.Append("Model generated successfully." + Environment.NewLine);
            }
            else
            {
                errors.Append("Please check the output window for errors" + Environment.NewLine);
            }

            if (revEngResult.EntityWarnings.Count > 0)
            {
                errors.Append("Please check the output window for warnings" + Environment.NewLine);
            }

            if (!string.IsNullOrEmpty(missingProviderPackage))
            {
                errors.AppendLine();
                errors.AppendFormat("The \"{0}\" NuGet package was not found in the project - it must be installed in order to build.", missingProviderPackage);
            }

            return errors.ToString();
        }

        private bool DropTemplates(string projectPath)
        {
            var toDir = Path.Combine(projectPath, "CodeTemplates");
            var fromDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (!Directory.Exists(toDir))
            {
                Directory.CreateDirectory(toDir);
                ZipFile.ExtractToDirectory(Path.Combine(fromDir, "CodeTemplates.zip"), toDir);
                return true;
            }

            return false;
        }

        public string WriteOptions(ReverseEngineerOptions options)
        {
            var ms = new MemoryStream();
            var ser = new DataContractJsonSerializer(typeof(ReverseEngineerOptions));
            ser.WriteObject(ms, options);
            byte[] json = ms.ToArray();
            ms.Close();
            return Encoding.UTF8.GetString(json, 0, json.Length);
        }

        public ReverseEngineerOptions TryReadOptions(string optionsPath)
        {
            if (!File.Exists(optionsPath)) return null;
            if (File.Exists(optionsPath + ".ignore")) return null;

            ReverseEngineerOptions deserializedOptions = null;
            try
            {
                var ms = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(optionsPath, Encoding.UTF8)));
                var ser = new DataContractJsonSerializer(typeof(ReverseEngineerOptions));
                deserializedOptions = ser.ReadObject(ms) as ReverseEngineerOptions;
                ms.Close();
            }
            catch (Exception ex)
            {
                _package.LogError(new List<string> { "Unable to load options" }, ex);
            }
            return deserializedOptions;
        }
    }
}