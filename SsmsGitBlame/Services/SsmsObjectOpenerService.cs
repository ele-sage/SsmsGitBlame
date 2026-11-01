using Community.VisualStudio.Toolkit;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Span = Microsoft.VisualStudio.Text.Span;

namespace SsmsGitBlame
{
    public sealed class SqlObjectIdentity
    {
        public string DatabaseName { get; set; }
        public string SchemaName { get; set; }
        public string ObjectName { get; set; }
        public string ObjectType { get; set; }
        public int GitCreateStatementLine0Based { get; set; }
    }

    public static class SsmsObjectOpenerService
    {
        private static readonly Regex CreateOrAlterRegex = new Regex(
            @"^(?<lead>\s*)(?:CREATE(?:\s+OR\s+ALTER)?|ALTER)\s+(?<type>PROC(?:EDURE)?|VIEW|FUNCTION|TRIGGER)\b",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

        // Cache of online database names per SQL Server instance: ServerDataSource -> HashSet<DatabaseName>
        private static readonly ConcurrentDictionary<string, HashSet<string>> ServerDatabasesCache
            = new ConcurrentDictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        private static object _cachedUiConnectionInfo;
        private static string _cachedWorkingConnectionString;

        /// <summary>
        /// Captures the active connection while a SQL Editor or Object Explorer is active.
        /// </summary>
        public static void CaptureActiveConnection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                object uiConn = GetScriptFactoryUiConnectionInfo();
                if (uiConn != null)
                {
                    _cachedUiConnectionInfo = uiConn;
                }

                string connStr = ResolveActiveConnectionString(null);
                if (!string.IsNullOrEmpty(connStr))
                {
                    _cachedWorkingConnectionString = connStr;
                }
            }
            catch { }
        }

        public static async Task OpenSearchResultForModifyAsync(SearchResultItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FullPath))
                return;

            SqlObjectIdentity identity = await Task.Run(() => ParseObjectIdentity(item));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // 1. Resolve connection string & UIConnectionInfo
            object uiConnInfo = GetScriptFactoryUiConnectionInfo()
                             ?? _cachedUiConnectionInfo
                             ?? GetObjectExplorerUiConnectionInfo();

            string baseConnString = ResolveActiveConnectionString(identity?.DatabaseName);

            string alterScript = null;
            bool loadedFromDatabase = false;

            if (identity != null && !string.IsNullOrEmpty(baseConnString))
            {
                // Idea #1: If the SQL file had no USE [db], match its folder path segments against cached sys.databases
                if (string.IsNullOrEmpty(identity.DatabaseName))
                {
                    identity.DatabaseName = await ResolveDatabaseNameFromPathAsync(baseConnString, item.FullPath, item.RelativePath);
                    Debug.WriteLine($"SsmsGitBlame: Matched DB from path against sys.databases: {identity.DatabaseName ?? "(none)"}");
                }

                // Idea #2: Fetch from resolved DB, or automatically search across all online databases if still unknown/not found
                var (fetchedScript, resolvedDbName) = await FetchLiveAlterScriptAcrossDatabasesAsync(baseConnString, identity);
                if (!string.IsNullOrEmpty(fetchedScript))
                {
                    alterScript = fetchedScript;
                    identity.DatabaseName = resolvedDbName;
                    loadedFromDatabase = true;
                }
            }

            // 2. Fallback to local Git file (converted to ALTER) if offline or not yet deployed to DB
            if (string.IsNullOrEmpty(alterScript) && File.Exists(item.FullPath))
            {
                string diskSql = await Task.Run(() => File.ReadAllText(item.FullPath));
                if (identity != null && string.IsNullOrEmpty(identity.DatabaseName))
                {
                    identity.DatabaseName = FallbackInferDatabaseFromRelativePath(item.RelativePath);
                }
                alterScript = ConvertDiskScriptToAlter(diskSql, identity?.DatabaseName);
            }

            if (string.IsNullOrEmpty(alterScript))
                return;

            // 3. Create new connected SQL Query Window in SSMS (targeting the resolved DatabaseName)
            bool openedBlankQuery = TryCreateNewConnectedBlankScript(uiConnInfo, identity?.DatabaseName);
            if (!openedBlankQuery)
            {
                await VS.Documents.OpenAsync(item.FullPath);
                return;
            }

            // 4. Populate the new query window and jump to the matched line
            DocumentView docView = await VS.Documents.GetActiveDocumentViewAsync();
            if (docView?.TextBuffer != null && docView.TextView != null)
            {
                using (ITextEdit edit = docView.TextBuffer.CreateEdit())
                {
                    edit.Replace(new Span(0, docView.TextBuffer.CurrentSnapshot.Length), alterScript);
                    edit.Apply();
                }

                NavigateToMatchedLineInBuffer(docView.TextView, item, identity);

                string sourceLabel = loadedFromDatabase
                    ? $"live DB [{identity.DatabaseName}]"
                    : "local Git file (not found on current SQL Server)";
                await VS.StatusBar.ShowMessageAsync(
                    $"SsmsGitBlame: Opened {identity?.SchemaName}.{identity?.ObjectName} from {sourceLabel}.");
            }
        }

        #region Multi-Database Resolution (Path Matching + Cross-Database Object Search)

        /// <summary>
        /// Idea #1: Fetches and caches all online database names on the server, then checks every folder
        /// segment in the file path to see if a folder matches a database on the server.
        /// </summary>
        private static async Task<string> ResolveDatabaseNameFromPathAsync(
            string baseConnectionString,
            string fullPath,
            string relativePath)
        {
            HashSet<string> serverDbs = await GetOrLoadServerDatabasesAsync(baseConnectionString).ConfigureAwait(false);
            if (serverDbs == null || serverDbs.Count == 0)
                return FallbackInferDatabaseFromRelativePath(relativePath);

            // Check relative path segments first (e.g. "vialisR_data0/PROCEDUREs/dbo.myProc.sql")
            if (!string.IsNullOrEmpty(relativePath))
            {
                string[] relSegments = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < relSegments.Length - 1; i++)
                {
                    if (serverDbs.TryGetValue(relSegments[i], out string actualDbName))
                        return actualDbName;
                }
            }

            // Check parent directories upwards from fullPath (e.g. when the repo root itself is named after the DB)
            if (!string.IsNullOrEmpty(fullPath))
            {
                DirectoryInfo dir = Directory.GetParent(fullPath);
                while (dir != null)
                {
                    if (serverDbs.TryGetValue(dir.Name, out string actualDbName))
                        return actualDbName;
                    dir = dir.Parent;
                }
            }

            return null;
        }

        private static async Task<HashSet<string>> GetOrLoadServerDatabasesAsync(string connectionString)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                string serverKey = builder.DataSource ?? "";

                if (ServerDatabasesCache.TryGetValue(serverKey, out HashSet<string> cached) && cached.Count > 0)
                {
                    return cached;
                }

                // Connect to master (or default DB) so we don't fail if InitialCatalog was invalid
                builder.InitialCatalog = "master";
                var dbSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var conn = new SqlConnection(builder.ConnectionString))
                {
                    await conn.OpenAsync().ConfigureAwait(false);
                    const string sql = "select [name] from sys.databases where [state] = 0 and [database_id] > 4;";
                    using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 4 })
                    using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync().ConfigureAwait(false))
                        {
                            if (!reader.IsDBNull(0))
                                dbSet.Add(reader.GetString(0));
                        }
                    }
                }

                if (dbSet.Count > 0)
                {
                    ServerDatabasesCache[serverKey] = dbSet;
                }
                return dbSet;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SsmsGitBlame: Failed to load sys.databases cache: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Idea #2: Queries the target database if known; otherwise (or if not found in that DB),
        /// searches across all online user databases in a single round-trip using cross-database OBJECT_ID().
        /// </summary>
        private static async Task<(string AlterScript, string ResolvedDbName)> FetchLiveAlterScriptAcrossDatabasesAsync(
            string baseConnectionString,
            SqlObjectIdentity identity)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(baseConnectionString);

                // 1. If we already matched a specific DatabaseName, try fetching directly from it first
                if (!string.IsNullOrWhiteSpace(identity.DatabaseName))
                {
                    builder.InitialCatalog = identity.DatabaseName;
                    string directScript = await TryFetchFromSingleDatabaseAsync(builder.ConnectionString, identity.DatabaseName, identity).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(directScript))
                    {
                        return (directScript, identity.DatabaseName);
                    }
                }

                // 2. Cross-Database Search: Find which database on this server owns [schema].[object]
                builder.InitialCatalog = "master";
                using (var conn = new SqlConnection(builder.ConnectionString))
                {
                    await conn.OpenAsync().ConfigureAwait(false);

                    // Fast single-query cross-database lookup using OBJECT_ID('[db].[schema].[obj]')
                    const string findAndFetchSql = @"
                        declare @targetDb sysname;
                        declare @fullObjName nvarchar(512) = quotename(@schema) + N'.' + quotename(@obj);

                        select top (1) @targetDb = d.[name]
                        from sys.databases as d
                        where d.[state] = 0
                          and d.[database_id] > 4
                          and has_dbaccess(d.[name]) = 1
                          and object_id(quotename(d.[name]) + N'.' + @fullObjName) is not null
                        order by 
                            case when d.[name] = @preferredDb then 0 else 1 end,
                            d.[database_id];

                        if @targetDb is not null
                        begin
                            declare @dynSql nvarchar(max) = N'
                                select 
                                    @dbOut as DatabaseName,
                                    m.definition,
                                    m.uses_ansi_nulls,
                                    m.uses_quoted_identifier
                                from ' + quotename(@targetDb) + N'.sys.sql_modules as m
                                where m.object_id = object_id(@fullThreePartName);';

                            declare @threePart nvarchar(776) = quotename(@targetDb) + N'.' + @fullObjName;

                            exec sp_executesql 
                                @stmt = @dynSql,
                                @params = N'@dbOut sysname, @fullThreePartName nvarchar(776)',
                                @dbOut = @targetDb,
                                @fullThreePartName = @threePart;
                        end";

                    using (var cmd = new SqlCommand(findAndFetchSql, conn) { CommandTimeout = 6 })
                    {
                        cmd.Parameters.AddWithValue("@schema", identity.SchemaName ?? "dbo");
                        cmd.Parameters.AddWithValue("@obj", identity.ObjectName ?? "");
                        cmd.Parameters.AddWithValue("@preferredDb", (object)identity.DatabaseName ?? DBNull.Value);

                        using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            if (await reader.ReadAsync().ConfigureAwait(false) && !reader.IsDBNull(1))
                            {
                                string foundDb = reader.GetString(0);
                                string definition = reader.GetString(1);
                                bool ansiNulls = !reader.IsDBNull(2) && reader.GetBoolean(2);
                                bool quotedId = !reader.IsDBNull(3) && reader.GetBoolean(3);

                                Debug.WriteLine($"SsmsGitBlame: Cross-DB search found {identity.SchemaName}.{identity.ObjectName} in [{foundDb}]");
                                string script = FormatCompleteAlterScript(foundDb, definition, ansiNulls, quotedId);
                                return (script, foundDb);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SsmsGitBlame: Cross-DB fetch exception: {ex.Message}");
            }

            return (null, identity.DatabaseName);
        }

        private static async Task<string> TryFetchFromSingleDatabaseAsync(
            string connectionString,
            string dbName,
            SqlObjectIdentity identity)
        {
            try
            {
                using (var conn = new SqlConnection(connectionString))
                {
                    await conn.OpenAsync().ConfigureAwait(false);

                    const string sql = @"
                        select 
                            m.definition,
                            m.uses_ansi_nulls,
                            m.uses_quoted_identifier
                        from sys.sql_modules as m
                        where m.object_id = object_id(@fullName);";

                    using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 4 })
                    {
                        cmd.Parameters.AddWithValue("@fullName", $"[{identity.SchemaName}].[{identity.ObjectName}]");
                        using (var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false))
                        {
                            if (!await reader.ReadAsync().ConfigureAwait(false) || reader.IsDBNull(0))
                                return null;

                            string definition = reader.GetString(0);
                            bool ansiNulls = !reader.IsDBNull(1) && reader.GetBoolean(1);
                            bool quotedId = !reader.IsDBNull(2) && reader.GetBoolean(2);

                            return FormatCompleteAlterScript(dbName, definition, ansiNulls, quotedId);
                        }
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static string FormatCompleteAlterScript(string dbName, string definition, bool ansiNulls, bool quotedId)
        {
            string alterBody = ConvertCreateToAlter(definition);
            return $"USE [{dbName}]\r\nGO\r\n" +
                   $"SET ANSI_NULLS {(ansiNulls ? "ON" : "OFF")}\r\nGO\r\n" +
                   $"SET QUOTED_IDENTIFIER {(quotedId ? "ON" : "OFF")}\r\nGO\r\n\r\n" +
                   alterBody.TrimStart('\r', '\n');
        }

        private static string FallbackInferDatabaseFromRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)) return null;
            string[] segments = relativePath.Replace('\\', '/').TrimStart('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 3)
            {
                string first = segments[0];
                // Ignore standard DACFx/SSDT top-level folders
                if (!first.Equals("dbo", StringComparison.OrdinalIgnoreCase) &&
                    !first.Equals("Programmability", StringComparison.OrdinalIgnoreCase) &&
                    !first.Equals("Stored Procedures", StringComparison.OrdinalIgnoreCase) &&
                    !first.Equals("PROCEDUREs", StringComparison.OrdinalIgnoreCase) &&
                    !first.Equals("Views", StringComparison.OrdinalIgnoreCase) &&
                    !first.Equals("Functions", StringComparison.OrdinalIgnoreCase))
                {
                    return first;
                }
            }
            return null;
        }

        #endregion

        #region AxialSqlTools-Style SSMS Connection Discovery

        private static Type FindLoadedSsmsType(string fullTypeName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = asm.GetType(fullTypeName, throwOnError: false);
                if (t != null) return t;
            }
            return null;
        }

        private static string ResolveActiveConnectionString(string targetDatabase)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Priority 1: Active Editor Window's UIConnectionInfo (or cached editor UIConnectionInfo)
            object uiConn = GetScriptFactoryUiConnectionInfo() ?? _cachedUiConnectionInfo;
            string connStr = ExtractConnectionStringFromUiConnectionInfo(uiConn, targetDatabase);
            if (!string.IsNullOrEmpty(connStr)) return connStr;

            // Priority 2: Selected Object Explorer Node's SqlOlapConnectionInfoBase.ConnectionString
            object oeConnInfo = GetObjectExplorerSqlConnectionInfo();
            connStr = ExtractConnectionStringFromSqlConnInfo(oeConnInfo, targetDatabase);
            if (!string.IsNullOrEmpty(connStr)) return connStr;

            // Priority 3: First connected server inside Object Explorer's hierarchy tree
            object firstTreeConn = GetFirstConnectedObjectExplorerNodeConnection();
            connStr = ExtractConnectionStringFromSqlConnInfo(firstTreeConn, targetDatabase);
            if (!string.IsNullOrEmpty(connStr)) return connStr;

            // Priority 4: Previously cached working connection string
            if (!string.IsNullOrEmpty(_cachedWorkingConnectionString))
            {
                return ApplyDatabaseAndCertSettings(_cachedWorkingConnectionString, targetDatabase);
            }

            return null;
        }

        private static object GetScriptFactoryUiConnectionInfo()
        {
            try
            {
                Type serviceCacheType = FindLoadedSsmsType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
                object scriptFactory = serviceCacheType?.GetProperty("ScriptFactory", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (scriptFactory == null) return null;

                object wndConnInfo = scriptFactory.GetType().GetProperty("CurrentlyActiveWndConnectionInfo")?.GetValue(scriptFactory);
                return wndConnInfo?.GetType().GetProperty("UIConnectionInfo")?.GetValue(wndConnInfo);
            }
            catch
            {
                return null;
            }
        }

        private static object GetObjectExplorerService()
        {
            Type oeInterface = FindLoadedSsmsType("Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService");
            return oeInterface != null ? Package.GetGlobalService(oeInterface) : null;
        }

        private static object GetObjectExplorerSqlConnectionInfo()
        {
            try
            {
                object oeService = GetObjectExplorerService();
                if (oeService == null) return null;

                MethodInfo getSelectedNodes = oeService.GetType().GetMethod("GetSelectedNodes");
                if (getSelectedNodes == null) return null;

                object[] parameters = new object[] { 0, null };
                getSelectedNodes.Invoke(oeService, parameters);

                if (parameters[1] is Array nodes && nodes.Length > 0)
                {
                    object firstNode = nodes.GetValue(0);
                    return firstNode?.GetType().GetProperty("Connection")?.GetValue(firstNode);
                }
            }
            catch { }
            return null;
        }

        private static object GetFirstConnectedObjectExplorerNodeConnection()
        {
            try
            {
                object oeService = GetObjectExplorerService();
                if (oeService == null) return null;

                PropertyInfo treeProp = oeService.GetType().GetProperty("Tree", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object tree = treeProp?.GetValue(oeService);
                if (tree == null) return null;

                PropertyInfo nodesProp = tree.GetType().GetProperty("Nodes");
                if (nodesProp?.GetValue(tree) is IEnumerable rootNodes)
                {
                    foreach (object rootNode in rootNodes)
                    {
                        object conn = TryGetConnectionFromTreeNode(rootNode);
                        if (conn != null) return conn;
                    }
                }
            }
            catch { }
            return null;
        }

        private static object TryGetConnectionFromTreeNode(object treeNode)
        {
            if (treeNode == null) return null;
            try
            {
                MethodInfo getService = treeNode.GetType().GetMethod("GetService");
                Type nodeInfoType = FindLoadedSsmsType("Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.INodeInformation");
                if (getService != null && nodeInfoType != null)
                {
                    object nodeInfo = getService.Invoke(treeNode, new object[] { nodeInfoType });
                    object conn = nodeInfo?.GetType().GetProperty("Connection")?.GetValue(nodeInfo);
                    if (conn != null) return conn;
                }
            }
            catch { }
            return null;
        }

        private static object GetObjectExplorerUiConnectionInfo()
        {
            try
            {
                object sqlConnInfo = GetObjectExplorerSqlConnectionInfo() ?? GetFirstConnectedObjectExplorerNodeConnection();
                if (sqlConnInfo == null) return null;

                Type uiConnType = FindLoadedSsmsType("Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo");
                if (uiConnType != null && uiConnType.IsInstanceOfType(sqlConnInfo))
                    return sqlConnInfo;
            }
            catch { }
            return null;
        }

        private static string ExtractConnectionStringFromSqlConnInfo(object sqlConnInfo, string targetDatabase)
        {
            if (sqlConnInfo == null) return null;
            try
            {
                string rawConnString = sqlConnInfo.GetType().GetProperty("ConnectionString")?.GetValue(sqlConnInfo) as string;
                if (!string.IsNullOrWhiteSpace(rawConnString))
                {
                    return ApplyDatabaseAndCertSettings(rawConnString, targetDatabase);
                }
            }
            catch { }
            return null;
        }

        private static string ExtractConnectionStringFromUiConnectionInfo(object uiConnInfo, string targetDatabase)
        {
            if (uiConnInfo == null) return null;
            try
            {
                Type t = uiConnInfo.GetType();
                string serverName = t.GetProperty("ServerName")?.GetValue(uiConnInfo) as string;
                if (string.IsNullOrWhiteSpace(serverName)) return null;

                string userName = t.GetProperty("UserName")?.GetValue(uiConnInfo) as string;
                string password = t.GetProperty("Password")?.GetValue(uiConnInfo) as string;

                if (string.IsNullOrEmpty(password))
                {
                    PropertyInfo inMemProp = t.GetProperty("InMemoryPassword", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    password = inMemProp?.GetValue(uiConnInfo) as string;

                    if (!string.IsNullOrEmpty(password))
                    {
                        Type utilType = t.Assembly.GetType("Microsoft.SqlServer.Management.Smo.RegSvrEnum.Util");
                        MethodInfo unprotect = utilType?.GetMethod("UnprotectData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (unprotect != null)
                        {
                            try { password = unprotect.Invoke(null, new object[] { password }) as string; } catch { }
                        }
                    }
                }

                object authTypeObj = t.GetProperty("AuthenticationType")?.GetValue(uiConnInfo);
                int authType = authTypeObj is int a ? a : (string.IsNullOrEmpty(userName) ? 0 : 1);

                string defaultDb = null;
                object advOptions = t.GetProperty("AdvancedOptions")?.GetValue(uiConnInfo);
                if (advOptions != null)
                {
                    PropertyInfo indexer = advOptions.GetType().GetProperty("Item", new[] { typeof(string) });
                    defaultDb = indexer?.GetValue(advOptions, new object[] { "DATABASE" }) as string;
                }

                var builder = new SqlConnectionStringBuilder
                {
                    DataSource = serverName,
                    InitialCatalog = !string.IsNullOrWhiteSpace(targetDatabase)
                        ? targetDatabase
                        : (!string.IsNullOrWhiteSpace(defaultDb) ? defaultDb : "master"),
                    IntegratedSecurity = (authType == 0) || string.IsNullOrEmpty(userName),
                    TrustServerCertificate = true,
                    ApplicationName = "SsmsGitBlame",
                    ConnectTimeout = 4
                };

                if (!builder.IntegratedSecurity)
                {
                    builder.UserID = userName;
                    if (!string.IsNullOrEmpty(password))
                        builder.Password = password;
                }

                return builder.ConnectionString;
            }
            catch
            {
                return null;
            }
        }

        private static string ApplyDatabaseAndCertSettings(string baseConnectionString, string targetDatabase)
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(baseConnectionString)
                {
                    TrustServerCertificate = true,
                    ConnectTimeout = 4
                };
                if (!string.IsNullOrWhiteSpace(targetDatabase))
                {
                    builder.InitialCatalog = targetDatabase;
                }
                return builder.ConnectionString;
            }
            catch
            {
                return baseConnectionString;
            }
        }

        private static bool TryCreateNewConnectedBlankScript(object uiConnInfo, string targetDatabase = null)
        {
            try
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Type serviceCacheType = FindLoadedSsmsType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
                object scriptFactory = serviceCacheType?.GetProperty("ScriptFactory", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (scriptFactory == null) return false;

                Type scriptTypeEnum = FindLoadedSsmsType("Microsoft.SqlServer.Management.UI.VSIntegration.Editors.ScriptType");
                if (scriptTypeEnum == null) return false;

                object sqlScriptType = Enum.Parse(scriptTypeEnum, "Sql");

                if (uiConnInfo != null)
                {
                    // Clone UIConnectionInfo and set its DATABASE option so the new SSMS tab connects directly to targetDatabase
                    object connToUse = CloneAndSetDatabaseOnUiConnInfo(uiConnInfo, targetDatabase) ?? uiConnInfo;

                    foreach (MethodInfo m in scriptFactory.GetType().GetMethods())
                    {
                        if (m.Name != "CreateNewBlankScript") continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length == 3 && ps[0].ParameterType == scriptTypeEnum && ps[1].ParameterType.IsInstanceOfType(connToUse))
                        {
                            m.Invoke(scriptFactory, new object[] { sqlScriptType, connToUse, null });
                            return true;
                        }
                    }
                }

                foreach (MethodInfo m in scriptFactory.GetType().GetMethods())
                {
                    if (m.Name != "CreateNewBlankScript") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType == scriptTypeEnum)
                    {
                        m.Invoke(scriptFactory, new object[] { sqlScriptType });
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private static object CloneAndSetDatabaseOnUiConnInfo(object uiConnInfo, string targetDatabase)
        {
            if (uiConnInfo == null || string.IsNullOrWhiteSpace(targetDatabase))
                return uiConnInfo;

            try
            {
                Type t = uiConnInfo.GetType();
                MethodInfo copyMethod = t.GetMethod("Copy", Type.EmptyTypes);
                object targetConn = copyMethod != null ? copyMethod.Invoke(uiConnInfo, null) : uiConnInfo;

                object advOptions = targetConn.GetType().GetProperty("AdvancedOptions")?.GetValue(targetConn);
                if (advOptions != null)
                {
                    // Set AdvancedOptions["DATABASE"] = targetDatabase
                    MethodInfo setMethod = advOptions.GetType().GetMethod("Set", new[] { typeof(string), typeof(string) });
                    if (setMethod != null)
                    {
                        setMethod.Invoke(advOptions, new object[] { "DATABASE", targetDatabase });
                    }
                    else
                    {
                        PropertyInfo indexer = advOptions.GetType().GetProperty("Item", new[] { typeof(string) });
                        indexer?.SetValue(advOptions, targetDatabase, new object[] { "DATABASE" });
                    }
                }

                return targetConn;
            }
            catch
            {
                return uiConnInfo;
            }
        }

        #endregion

        #region Script Formatting & Line Mapping

        private static string ConvertCreateToAlter(string sqlDefinition)
        {
            if (string.IsNullOrEmpty(sqlDefinition)) return sqlDefinition;

            var parsed = SqlScriptDomParser.ParseHeader(sqlDefinition);
            if (parsed != null && !parsed.DdlVerbText.Equals("ALTER", StringComparison.OrdinalIgnoreCase))
            {
                return sqlDefinition.Substring(0, parsed.StatementStartOffset)
                     + "ALTER"
                     + sqlDefinition.Substring(parsed.StatementStartOffset + parsed.DdlVerbLength);
            }

            return sqlDefinition;
        }

        private static string ConvertDiskScriptToAlter(string diskSql, string dbName)
        {
            string body = ConvertCreateToAlter(diskSql);
            if (!string.IsNullOrEmpty(dbName) &&
                !Regex.IsMatch(body, @"^\s*USE\s+\[?", RegexOptions.IgnoreCase | RegexOptions.Multiline))
            {
                return $"USE [{dbName}]\r\nGO\r\n{body}";
            }
            return body;
        }

        private static void NavigateToMatchedLineInBuffer(IWpfTextView textView, SearchResultItem item, SqlObjectIdentity identity)
        {
            ITextSnapshot snapshot = textView.TextSnapshot;
            if (snapshot.LineCount == 0) return;

            int targetLine0Based = Math.Max(0, item.LineNumber - 1);

            if (identity != null)
            {
                Match bufferCreateMatch = CreateOrAlterRegex.Match(snapshot.GetText());
                if (bufferCreateMatch.Success)
                {
                    int bufferCreateLine0Based = snapshot.GetLineNumberFromPosition(bufferCreateMatch.Index);
                    int relativeOffsetFromCreate = (item.LineNumber - 1) - identity.GitCreateStatementLine0Based;
                    if (relativeOffsetFromCreate >= 0)
                    {
                        targetLine0Based = bufferCreateLine0Based + relativeOffsetFromCreate;
                    }
                }
            }

            targetLine0Based = Math.Max(0, Math.Min(targetLine0Based, snapshot.LineCount - 1));

            string expectedSnippet = item.LineContent?.Trim();
            if (!string.IsNullOrEmpty(expectedSnippet))
            {
                int bestLine = FindClosestMatchingLine(snapshot, targetLine0Based, expectedSnippet, searchRadius: 25);
                if (bestLine >= 0) targetLine0Based = bestLine;
            }

            ITextSnapshotLine line = snapshot.GetLineFromLineNumber(targetLine0Based);
            SnapshotSpan targetSpan = new SnapshotSpan(line.Start, line.End);

            if (item.HighlightRegex != null)
            {
                Match match = item.HighlightRegex.Match(line.GetText());
                if (match.Success && match.Length > 0)
                {
                    targetSpan = new SnapshotSpan(line.Start + match.Index, match.Length);
                    textView.Selection.Select(targetSpan, isReversed: false);
                    textView.Caret.MoveTo(targetSpan.End);
                }
                else
                {
                    textView.Selection.Clear();
                    textView.Caret.MoveTo(line.Start);
                }
            }
            else
            {
                textView.Caret.MoveTo(line.Start);
            }

            textView.ViewScroller.EnsureSpanVisible(targetSpan, EnsureSpanVisibleOptions.AlwaysCenter);
        }

        private static int FindClosestMatchingLine(ITextSnapshot snapshot, int estimatedLine, string expectedTrimmed, int searchRadius)
        {
            for (int delta = 0; delta <= searchRadius; delta++)
            {
                int down = estimatedLine + delta;
                if (down < snapshot.LineCount &&
                    snapshot.GetLineFromLineNumber(down).GetText().IndexOf(expectedTrimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                    return down;

                if (delta > 0)
                {
                    int up = estimatedLine - delta;
                    if (up >= 0 &&
                        snapshot.GetLineFromLineNumber(up).GetText().IndexOf(expectedTrimmed, StringComparison.OrdinalIgnoreCase) >= 0)
                        return up;
                }
            }
            return -1;
        }

        private static SqlObjectIdentity ParseObjectIdentity(SearchResultItem item)
        {
            if (File.Exists(item.FullPath))
            {
                try
                {
                    string diskText = File.ReadAllText(item.FullPath);
                    var parsed = SqlScriptDomParser.ParseHeader(diskText);
                    if (parsed != null)
                    {
                        return new SqlObjectIdentity
                        {
                            DatabaseName = parsed.DatabaseName,
                            SchemaName = parsed.SchemaName,
                            ObjectName = parsed.ObjectName,
                            ObjectType = parsed.ObjectType.ToString().ToUpperInvariant(),
                            GitCreateStatementLine0Based = parsed.StatementStartLine0Based
                        };
                    }
                }
                catch { }
            }

            string fileNameNoExt = Path.GetFileNameWithoutExtension(item.FullPath) ?? "";
            string schema = "dbo";
            string objName = fileNameNoExt;

            int dotIndex = fileNameNoExt.IndexOf('.');
            if (dotIndex > 0 && dotIndex < fileNameNoExt.Length - 1)
            {
                schema = fileNameNoExt.Substring(0, dotIndex);
                objName = fileNameNoExt.Substring(dotIndex + 1);
            }

            return new SqlObjectIdentity
            {
                DatabaseName = null,
                SchemaName = schema,
                ObjectName = objName,
                ObjectType = "PROCEDURE",
                GitCreateStatementLine0Based = 0
            };
        }

        #endregion
    }
}