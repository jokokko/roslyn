﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeStyle;
using Microsoft.CodeAnalysis.Diagnostics.Log;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Options;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.SolutionCrawler;
using Microsoft.CodeAnalysis.Workspaces.Diagnostics;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Diagnostics.EngineV2
{
    /// <summary>
    /// Diagnostic Analyzer Engine V2
    /// 
    /// This one follows pattern compiler has set for diagnostic analyzer.
    /// </summary>
    internal partial class DiagnosticIncrementalAnalyzer : IIncrementalAnalyzer
    {
        private readonly int _correlationId;

        private readonly StateManager _stateManager;
        private readonly Executor _executor;
        private readonly CompilationManager _compilationManager;

        public DiagnosticIncrementalAnalyzer(
            DiagnosticAnalyzerService owner,
            int correlationId,
            Workspace workspace,
            HostAnalyzerManager hostAnalyzerManager,
            AbstractHostDiagnosticUpdateSource hostDiagnosticUpdateSource)
        {
            Contract.ThrowIfNull(owner);

            this.Owner = owner;
            this.Workspace = workspace;
            this.HostAnalyzerManager = hostAnalyzerManager;
            this.HostDiagnosticUpdateSource = hostDiagnosticUpdateSource;
            this.DiagnosticLogAggregator = new DiagnosticLogAggregator(owner);

            _correlationId = correlationId;

            _stateManager = new StateManager(hostAnalyzerManager);
            _stateManager.ProjectAnalyzerReferenceChanged += OnProjectAnalyzerReferenceChanged;

            _executor = new Executor(this);
            _compilationManager = new CompilationManager(this);
        }

        internal DiagnosticAnalyzerService Owner { get; }
        internal Workspace Workspace { get; }
        internal AbstractHostDiagnosticUpdateSource HostDiagnosticUpdateSource { get; }
        internal HostAnalyzerManager HostAnalyzerManager { get; }
        internal DiagnosticLogAggregator DiagnosticLogAggregator { get; private set; }

        public bool ContainsDiagnostics(Workspace workspace, ProjectId projectId)
        {
            foreach (var stateSet in _stateManager.GetStateSets(projectId))
            {
                if (stateSet.ContainsAnyDocumentOrProjectDiagnostics(projectId))
                {
                    return true;
                }
            }

            return false;
        }

        public bool NeedsReanalysisOnOptionChanged(object sender, OptionChangedEventArgs e)
        {
            return e.Option.Feature == nameof(SimplificationOptions) ||
                   e.Option.Feature == nameof(CodeStyleOptions) ||
                   e.Option == ServiceFeatureOnOffOptions.ClosedFileDiagnostic ||
                   e.Option == RuntimeOptions.FullSolutionAnalysis;
        }

        private bool SupportAnalysisKind(DiagnosticAnalyzer analyzer, string language, AnalysisKind kind)
        {
            // compiler diagnostic analyzer always support all kinds
            if (HostAnalyzerManager.IsCompilerDiagnosticAnalyzer(language, analyzer))
            {
                return true;
            }

            switch (kind)
            {
                case AnalysisKind.Syntax:
                    return analyzer.SupportsSyntaxDiagnosticAnalysis();
                case AnalysisKind.Semantic:
                    return analyzer.SupportsSemanticDiagnosticAnalysis();
                default:
                    return Contract.FailWithReturn<bool>("shouldn't reach here");
            }
        }

        private void OnProjectAnalyzerReferenceChanged(object sender, ProjectAnalyzerReferenceChangedEventArgs e)
        {
            if (e.Removed.Length == 0)
            {
                // nothing to refresh
                return;
            }

            // events will be automatically serialized.
            var project = e.Project;
            var stateSets = e.Removed;

            // make sure we drop cache related to the analyzers
            foreach (var stateSet in stateSets)
            {
                stateSet.OnRemoved();
            }

            ClearAllDiagnostics(stateSets, project.Id);
        }

        public void Shutdown()
        {
            var stateSets = _stateManager.GetStateSets();

            Owner.RaiseBulkDiagnosticsUpdated(raiseEvents =>
            {
                var handleActiveFile = true;
                foreach (var stateSet in stateSets)
                {
                    var projectIds = stateSet.GetProjectsWithDiagnostics();
                    foreach (var projectId in projectIds)
                    {
                        RaiseProjectDiagnosticsRemoved(stateSet, projectId, stateSet.GetDocumentsWithDiagnostics(projectId), handleActiveFile, raiseEvents);
                    }
                }
            });
        }

        private void ClearAllDiagnostics(ImmutableArray<StateSet> stateSets, ProjectId projectId)
        {
            Owner.RaiseBulkDiagnosticsUpdated(raiseEvents =>
            {
                var handleActiveFile = true;
                foreach (var stateSet in stateSets)
                {
                    // PERF: don't fire events for ones that we dont have any diagnostics on
                    if (!stateSet.ContainsAnyDocumentOrProjectDiagnostics(projectId))
                    {
                        continue;
                    }

                    RaiseProjectDiagnosticsRemoved(stateSet, projectId, stateSet.GetDocumentsWithDiagnostics(projectId), handleActiveFile, raiseEvents);
                }
            });
        }

        private void RaiseDiagnosticsCreated(
            Project project, StateSet stateSet, ImmutableArray<DiagnosticData> items, Action<DiagnosticsUpdatedArgs> raiseEvents)
        {
            Contract.ThrowIfFalse(project.Solution.Workspace == Workspace);

            raiseEvents(DiagnosticsUpdatedArgs.DiagnosticsCreated(
                CreateId(stateSet.Analyzer, project.Id, AnalysisKind.NonLocal, stateSet.ErrorSourceName),
                project.Solution.Workspace,
                project.Solution,
                project.Id,
                documentId: null,
                diagnostics: items));
        }

        private void RaiseDiagnosticsRemoved(
            ProjectId projectId, Solution solution, StateSet stateSet, Action<DiagnosticsUpdatedArgs> raiseEvents)
        {
            Contract.ThrowIfFalse(solution == null || solution.Workspace == Workspace);

            raiseEvents(DiagnosticsUpdatedArgs.DiagnosticsRemoved(
                CreateId(stateSet.Analyzer, projectId, AnalysisKind.NonLocal, stateSet.ErrorSourceName),
                Workspace,
                solution,
                projectId,
                documentId: null));
        }

        private void RaiseDiagnosticsCreated(
            Document document, StateSet stateSet, AnalysisKind kind, ImmutableArray<DiagnosticData> items, Action<DiagnosticsUpdatedArgs> raiseEvents)
        {
            Contract.ThrowIfFalse(document.Project.Solution.Workspace == Workspace);

            raiseEvents(DiagnosticsUpdatedArgs.DiagnosticsCreated(
                CreateId(stateSet.Analyzer, document.Id, kind, stateSet.ErrorSourceName),
                document.Project.Solution.Workspace,
                document.Project.Solution,
                document.Project.Id,
                document.Id,
                items));
        }

        private void RaiseDiagnosticsRemoved(
            DocumentId documentId, Solution solution, StateSet stateSet, AnalysisKind kind, Action<DiagnosticsUpdatedArgs> raiseEvents)
        {
            Contract.ThrowIfFalse(solution == null || solution.Workspace == Workspace);

            raiseEvents(DiagnosticsUpdatedArgs.DiagnosticsRemoved(
                CreateId(stateSet.Analyzer, documentId, kind, stateSet.ErrorSourceName),
                Workspace,
                solution,
                documentId.ProjectId,
                documentId));
        }

        private object CreateId(DiagnosticAnalyzer analyzer, DocumentId key, AnalysisKind kind, string errorSourceName)
        {
            return CreateIdInternal(analyzer, key, kind, errorSourceName);
        }

        private object CreateId(DiagnosticAnalyzer analyzer, ProjectId key, AnalysisKind kind, string errorSourceName)
        {
            return CreateIdInternal(analyzer, key, kind, errorSourceName);
        }

        private static object CreateIdInternal(DiagnosticAnalyzer analyzer, object key, AnalysisKind kind, string errorSourceName)
        {
            return new LiveDiagnosticUpdateArgsId(analyzer, key, (int)kind, errorSourceName);
        }

        public static Task<VersionStamp> GetDiagnosticVersionAsync(Project project, CancellationToken cancellationToken)
        {
            return project.GetDependentVersionAsync(cancellationToken);
        }

        private static DiagnosticAnalysisResult GetResultOrEmpty(ImmutableDictionary<DiagnosticAnalyzer, DiagnosticAnalysisResult> map, DiagnosticAnalyzer analyzer, ProjectId projectId, VersionStamp version)
        {
            if (map.TryGetValue(analyzer, out var result))
            {
                return result;
            }

            return new DiagnosticAnalysisResult(projectId, version);
        }

        private static ImmutableArray<DiagnosticData> GetResult(DiagnosticAnalysisResult result, AnalysisKind kind, DocumentId id)
        {
            if (result.IsEmpty || !result.DocumentIds.Contains(id) || result.IsAggregatedForm)
            {
                return ImmutableArray<DiagnosticData>.Empty;
            }

            switch (kind)
            {
                case AnalysisKind.Syntax:
                    return result.GetResultOrEmpty(result.SyntaxLocals, id);
                case AnalysisKind.Semantic:
                    return result.GetResultOrEmpty(result.SemanticLocals, id);
                case AnalysisKind.NonLocal:
                    return result.GetResultOrEmpty(result.NonLocals, id);
                default:
                    return Contract.FailWithReturn<ImmutableArray<DiagnosticData>>("shouldn't reach here");
            }
        }

        public void LogAnalyzerCountSummary()
        {
            DiagnosticAnalyzerLogger.LogAnalyzerCrashCountSummary(_correlationId, DiagnosticLogAggregator);
            DiagnosticAnalyzerLogger.LogAnalyzerTypeCountSummary(_correlationId, DiagnosticLogAggregator);

            // reset the log aggregator
            ResetDiagnosticLogAggregator();
        }

        private void ResetDiagnosticLogAggregator()
        {
            DiagnosticLogAggregator = new DiagnosticLogAggregator(Owner);
        }

        // internal for testing purposes.
        internal Action<Exception, DiagnosticAnalyzer, Diagnostic> GetOnAnalyzerException(ProjectId projectId)
        {
            return Owner.GetOnAnalyzerException(projectId, DiagnosticLogAggregator);
        }

        internal IEnumerable<DiagnosticAnalyzer> GetAnalyzersTestOnly(Project project)
        {
            return _stateManager.GetOrCreateStateSets(project).Select(s => s.Analyzer);
        }

        private static string GetDocumentLogMessage(string title, Document document, DiagnosticAnalyzer analyzer)
        {
            return $"{title}: ({document.FilePath ?? document.Name}), ({analyzer.ToString()})";
        }

        private static string GetProjectLogMessage(Project project, IEnumerable<StateSet> stateSets)
        {
            return $"project: ({project.FilePath ?? project.Name}), ({string.Join(Environment.NewLine, stateSets.Select(s => s.Analyzer.ToString()))})";
        }

        private static string GetResetLogMessage(Document document)
        {
            return $"document close/reset: ({document.FilePath ?? document.Name})";
        }

        private static string GetOpenLogMessage(Document document)
        {
            return $"document open: ({document.FilePath ?? document.Name})";
        }

        private static string GetRemoveLogMessage(DocumentId id)
        {
            return $"document remove: {id.ToString()}";
        }

        private static string GetRemoveLogMessage(ProjectId id)
        {
            return $"project remove: {id.ToString()}";
        }
    }
}
