using Microsoft.SqlServer.TransactSql.ScriptDom;
using System;
using System.Collections.Generic;
using System.IO;

namespace SsmsGitBlame
{
    public enum SqlDdlObjectType
    {
        Procedure,
        View,
        Function,
        Trigger
    }

    public sealed class ParsedSqlObjectHeader
    {
        public string DatabaseName { get; set; }
        public string SchemaName { get; set; }
        public string ObjectName { get; set; }
        public SqlDdlObjectType ObjectType { get; set; }

        /// <summary>
        /// Exact character offset where CREATE / ALTER starts in the SQL string.
        /// </summary>
        public int StatementStartOffset { get; set; }

        /// <summary>
        /// 0-based line index where CREATE / ALTER starts.
        /// </summary>
        public int StatementStartLine0Based { get; set; }

        /// <summary>
        /// The exact DDL verb used in the script ("CREATE", "ALTER", or "CREATE OR ALTER").
        /// </summary>
        public string DdlVerbText { get; set; }

        /// <summary>
        /// Length of the DDL verb in the original script so we can swap CREATE <-> ALTER cleanly.
        /// </summary>
        public int DdlVerbLength { get; set; }

        /// <summary>
        /// Standard DACFx / SSDT folder name ("Stored Procedures", "Views", "Functions", "Triggers").
        /// </summary>
        public string DacFxFolderName => ObjectType switch
        {
            SqlDdlObjectType.Procedure => "Stored Procedures",
            SqlDdlObjectType.View => "Views",
            SqlDdlObjectType.Function => "Functions",
            SqlDdlObjectType.Trigger => "Triggers",
            _ => "Stored Procedures"
        };

        /// <summary>
        /// Legacy uppercase plural folder name ("PROCEDUREs", "VIEWs", "FUNCTIONs", "TRIGGERs").
        /// </summary>
        public string LegacyFolderName => ObjectType switch
        {
            SqlDdlObjectType.Procedure => "PROCEDUREs",
            SqlDdlObjectType.View => "VIEWs",
            SqlDdlObjectType.Function => "FUNCTIONs",
            SqlDdlObjectType.Trigger => "TRIGGERs",
            _ => "PROCEDUREs"
        };
    }

    public static class SqlScriptDomParser
    {
        /// <summary>
        /// Parses a T-SQL buffer using ScriptDom and extracts the active USE [Database] 
        /// and the first CREATE/ALTER PROCEDURE, VIEW, FUNCTION, or TRIGGER statement.
        /// Completely immune to block comments, line comments, and string literals.
        /// </summary>
        public static ParsedSqlObjectHeader ParseHeader(string sqlText)
        {
            if (string.IsNullOrWhiteSpace(sqlText))
                return null;

            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            TSqlFragment fragment;

            using (var reader = new StringReader(sqlText))
            {
                fragment = parser.Parse(reader, out IList<ParseError> _);
            }

            if (fragment == null)
                return null;

            var visitor = new HeaderVisitor(sqlText, fragment.ScriptTokenStream);
            fragment.Accept(visitor);
            return visitor.Result;
        }

        public static string ExtractDatabaseName(string sqlText)
        {
            if (string.IsNullOrWhiteSpace(sqlText))
                return null;

            // Only need to parse the top of the file if we just want USE [db]
            string slice = sqlText.Length > 8000 ? sqlText.Substring(0, 8000) : sqlText;
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using (var reader = new StringReader(slice))
            {
                TSqlFragment fragment = parser.Parse(reader, out IList<ParseError> _);
                if (fragment == null) return null;

                var dbVisitor = new UseDatabaseVisitor();
                fragment.Accept(dbVisitor);
                return dbVisitor.DatabaseName;
            }
        }

        private sealed class UseDatabaseVisitor : TSqlFragmentVisitor
        {
            public string DatabaseName { get; private set; }

            public override void ExplicitVisit(UseStatement node)
            {
                if (DatabaseName == null && node.DatabaseName != null)
                {
                    DatabaseName = node.DatabaseName.Value;
                }
            }
        }

        private sealed class HeaderVisitor : TSqlFragmentVisitor
        {
            private readonly string _sqlText;
            private readonly IList<TSqlParserToken> _tokens;
            private string _lastUseDatabase;

            public ParsedSqlObjectHeader Result { get; private set; }

            public HeaderVisitor(string sqlText, IList<TSqlParserToken> tokens)
            {
                _sqlText = sqlText;
                _tokens = tokens;
            }

            public override void ExplicitVisit(UseStatement node)
            {
                if (Result == null && node.DatabaseName != null)
                {
                    _lastUseDatabase = node.DatabaseName.Value;
                }
            }

            public override void ExplicitVisit(CreateProcedureStatement node)
                => CaptureObject(node, node.ProcedureReference?.Name, SqlDdlObjectType.Procedure);

            public override void ExplicitVisit(AlterProcedureStatement node)
                => CaptureObject(node, node.ProcedureReference?.Name, SqlDdlObjectType.Procedure);

            public override void ExplicitVisit(CreateOrAlterProcedureStatement node)
                => CaptureObject(node, node.ProcedureReference?.Name, SqlDdlObjectType.Procedure);

            public override void ExplicitVisit(CreateViewStatement node)
                => CaptureObject(node, node.SchemaObjectName, SqlDdlObjectType.View);

            public override void ExplicitVisit(AlterViewStatement node)
                => CaptureObject(node, node.SchemaObjectName, SqlDdlObjectType.View);

            public override void ExplicitVisit(CreateOrAlterViewStatement node)
                => CaptureObject(node, node.SchemaObjectName, SqlDdlObjectType.View);

            public override void ExplicitVisit(CreateFunctionStatement node)
                => CaptureObject(node, node.Name, SqlDdlObjectType.Function);

            public override void ExplicitVisit(AlterFunctionStatement node)
                => CaptureObject(node, node.Name, SqlDdlObjectType.Function);

            public override void ExplicitVisit(CreateOrAlterFunctionStatement node)
                => CaptureObject(node, node.Name, SqlDdlObjectType.Function);

            public override void ExplicitVisit(CreateTriggerStatement node)
                => CaptureObject(node, node.Name, SqlDdlObjectType.Trigger);

            public override void ExplicitVisit(AlterTriggerStatement node)
                => CaptureObject(node, node.Name, SqlDdlObjectType.Trigger);

            public override void ExplicitVisit(CreateOrAlterTriggerStatement node)
                => CaptureObject(node, node.Name, SqlDdlObjectType.Trigger);

            private void CaptureObject(TSqlStatement node, SchemaObjectName nameNode, SqlDdlObjectType objType)
            {
                if (Result != null || nameNode?.BaseIdentifier == null)
                    return;

                string schema = nameNode.SchemaIdentifier?.Value ?? "dbo";
                string objName = nameNode.BaseIdentifier.Value;
                string dbName = nameNode.DatabaseIdentifier?.Value ?? _lastUseDatabase;

                // Measure the exact span of "CREATE", "ALTER", or "CREATE OR ALTER" before PROC/VIEW/FUNCTION/TRIGGER
                ExtractDdlVerbSpan(node, out int startOffset, out int verbLength, out string verbText);

                Result = new ParsedSqlObjectHeader
                {
                    DatabaseName = dbName,
                    SchemaName = schema,
                    ObjectName = objName,
                    ObjectType = objType,
                    StatementStartOffset = startOffset,
                    StatementStartLine0Based = Math.Max(0, node.StartLine - 1),
                    DdlVerbLength = verbLength,
                    DdlVerbText = verbText
                };
            }

            private void ExtractDdlVerbSpan(TSqlStatement node, out int startOffset, out int verbLength, out string verbText)
            {
                startOffset = node.StartOffset;
                verbLength = 6; // fallback "CREATE"
                verbText = "CREATE";

                if (_tokens == null || node.FirstTokenIndex < 0 || node.FirstTokenIndex >= _tokens.Count)
                    return;

                TSqlParserToken firstToken = _tokens[node.FirstTokenIndex];
                startOffset = firstToken.Offset;
                int lastVerbTokenEnd = firstToken.Offset + (firstToken.Text?.Length ?? 0);

                // Walk tokens from FirstTokenIndex up to PROC / PROCEDURE / VIEW / FUNCTION / TRIGGER
                for (int i = node.FirstTokenIndex + 1; i <= node.LastTokenIndex && i < _tokens.Count; i++)
                {
                    TSqlParserToken tok = _tokens[i];
                    if (tok.TokenType == TSqlTokenType.WhiteSpace ||
                        tok.TokenType == TSqlTokenType.SingleLineComment ||
                        tok.TokenType == TSqlTokenType.MultilineComment)
                    {
                        continue;
                    }

                    if (tok.TokenType == TSqlTokenType.Or || tok.TokenType == TSqlTokenType.Alter)
                    {
                        lastVerbTokenEnd = tok.Offset + (tok.Text?.Length ?? 0);
                        continue;
                    }

                    // Reached PROC / PROCEDURE / VIEW / FUNCTION / TRIGGER
                    break;
                }

                verbLength = Math.Max(1, lastVerbTokenEnd - startOffset);
                if (startOffset >= 0 && startOffset + verbLength <= _sqlText.Length)
                {
                    verbText = _sqlText.Substring(startOffset, verbLength);
                }
            }
        }
    }
}